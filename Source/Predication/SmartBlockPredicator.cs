using Microsoft.Boogie.GraphUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.Boogie {

public class SmartBlockPredicator {

  Program prog;
  Implementation impl;
  Graph<Block> blockGraph;
  List<Tuple<Block, bool>> sortedBlocks;

  Func<Procedure, bool> useProcedurePredicates;

  Dictionary<Block, Variable> predMap, defMap;
  Dictionary<Block, HashSet<Variable>> ownedMap;
  Dictionary<Block, Block> parentMap;
  Dictionary<Block, PartInfo> partInfo;

  IdentifierExpr fp;
  Dictionary<Microsoft.Boogie.Type, IdentifierExpr> havocVars =
    new Dictionary<Microsoft.Boogie.Type, IdentifierExpr>();
  Dictionary<Block, Expr> blockIds = new Dictionary<Block, Expr>();
  HashSet<Block> doneBlocks = new HashSet<Block>();
  bool myUseProcedurePredicates;
  UniformityAnalyser uni;

  SmartBlockPredicator(Program p, Implementation i, Func<Procedure, bool> upp, UniformityAnalyser u) {
    prog = p;
    impl = i;
    useProcedurePredicates = upp;
    myUseProcedurePredicates = useProcedurePredicates(i.Proc);
    uni = u;
  }

  void PredicateCmd(Expr p, List<Block> blocks, Block block, Cmd cmd, out Block nextBlock) {
    var cCmd = cmd as CallCmd;
    if (cCmd != null && !useProcedurePredicates(cCmd.Proc)) {
      if (p == null) {
        block.Cmds.Add(cmd);
        nextBlock = block;
        return;
      }

      var trueBlock = new Block();
      blocks.Add(trueBlock);
      trueBlock.Label = block.Label + ".call.true";
      trueBlock.Cmds.Add(new AssumeCmd(Token.NoToken, p));
      trueBlock.Cmds.Add(cmd);

      var falseBlock = new Block();
      blocks.Add(falseBlock);
      falseBlock.Label = block.Label + ".call.false";
      falseBlock.Cmds.Add(new AssumeCmd(Token.NoToken, Expr.Not(p)));

      var contBlock = new Block();
      blocks.Add(contBlock);
      contBlock.Label = block.Label + ".call.cont";

      block.TransferCmd =
        new GotoCmd(Token.NoToken, new BlockSeq(trueBlock, falseBlock));
      trueBlock.TransferCmd = falseBlock.TransferCmd =
        new GotoCmd(Token.NoToken, new BlockSeq(contBlock));
      nextBlock = contBlock;
    } else {
      PredicateCmd(p, block.Cmds, cmd);
      nextBlock = block;
    }
  }

  void PredicateCmd(Expr p, CmdSeq cmdSeq, Cmd cmd) {
    if (cmd is CallCmd) {
      var cCmd = (CallCmd)cmd;
      Debug.Assert(useProcedurePredicates(cCmd.Proc));
      cCmd.Ins.Insert(0, p != null ? p : Expr.True);
      cmdSeq.Add(cCmd);
    } else if (p == null) {
      cmdSeq.Add(cmd);
    } else if (cmd is AssignCmd) {
      var aCmd = (AssignCmd)cmd;
      cmdSeq.Add(new AssignCmd(Token.NoToken, aCmd.Lhss,
                   new List<Expr>(aCmd.Lhss.Zip(aCmd.Rhss, (lhs, rhs) =>
                     new NAryExpr(Token.NoToken,
                       new IfThenElse(Token.NoToken),
                       new ExprSeq(p, rhs, lhs.AsExpr))))));
    } else if (cmd is AssertCmd) {
      var aCmd = (AssertCmd)cmd;
      Expr newExpr = new EnabledReplacementVisitor(p).VisitExpr(aCmd.Expr);
      aCmd.Expr = QKeyValue.FindBoolAttribute(aCmd.Attributes, "do_not_predicate") ? newExpr : Expr.Imp(p, newExpr);
      cmdSeq.Add(aCmd);
    } else if (cmd is AssumeCmd) {
      var aCmd = (AssumeCmd)cmd;
      cmdSeq.Add(new AssumeCmd(Token.NoToken, Expr.Imp(p, aCmd.Expr)));
    } else if (cmd is HavocCmd) {
      var hCmd = (HavocCmd)cmd;
      foreach (IdentifierExpr v in hCmd.Vars) {
        Microsoft.Boogie.Type type = v.Decl.TypedIdent.Type;
        Contract.Assert(type != null);

        IdentifierExpr havocTempExpr;
        if (havocVars.ContainsKey(type)) {
          havocTempExpr = havocVars[type];
        } else {
          var havocVar = new LocalVariable(Token.NoToken,
                             new TypedIdent(Token.NoToken,
                                            "_HAVOC_" + type.ToString(), type));
          impl.LocVars.Add(havocVar);
          havocVars[type] = havocTempExpr =
            new IdentifierExpr(Token.NoToken, havocVar);
        }
        cmdSeq.Add(new HavocCmd(Token.NoToken,
                                new IdentifierExprSeq(havocTempExpr)));
        cmdSeq.Add(Cmd.SimpleAssign(Token.NoToken, v,
                                    new NAryExpr(Token.NoToken,
                                      new IfThenElse(Token.NoToken),
                                      new ExprSeq(p, havocTempExpr, v))));
      }
    } else if (cmd is CommentCmd) {
      // skip
    } else if (cmd is StateCmd) {
      var sCmd = (StateCmd)cmd;
      var newCmdSeq = new CmdSeq();
      foreach (Cmd c in sCmd.Cmds)
        PredicateCmd(p, newCmdSeq, c);
      sCmd.Cmds = newCmdSeq;
      cmdSeq.Add(sCmd);
    } else {
      Console.WriteLine("Unsupported cmd: " + cmd.GetType().ToString());
    }
  }

  // hasPredicatedRegion is true iff the block or its targets are predicated
  // (i.e. we enter, stay within or exit a predicated region).
  void PredicateTransferCmd(Expr p, Block src, CmdSeq cmdSeq, TransferCmd cmd, out bool hasPredicatedRegion) {
    hasPredicatedRegion = predMap.ContainsKey(src);

    if (cmd is GotoCmd) {
      var gCmd = (GotoCmd)cmd;

      hasPredicatedRegion = hasPredicatedRegion ||
        gCmd.labelTargets.Cast<Block>().Any(b => predMap.ContainsKey(b));

      if (gCmd.labelTargets.Length == 1) {
        if (defMap.ContainsKey(gCmd.labelTargets[0]))
          PredicateCmd(p, cmdSeq,
                       Cmd.SimpleAssign(Token.NoToken,
                                        Expr.Ident(predMap[gCmd.labelTargets[0]]), Expr.True));
      } else {
        Debug.Assert(gCmd.labelTargets.Length > 1);
        Debug.Assert(gCmd.labelTargets.Cast<Block>().All(t => uni.IsUniform(impl.Name, t) ||
                                                              partInfo.ContainsKey(t)));
        foreach (Block target in gCmd.labelTargets) {
          if (!partInfo.ContainsKey(target))
            continue;

          var part = partInfo[target];
          if (defMap.ContainsKey(part.realDest))
            PredicateCmd(p, cmdSeq,
                         Cmd.SimpleAssign(Token.NoToken,
                                          Expr.Ident(predMap[part.realDest]), part.pred));
          var predsExitingLoop = new Dictionary<Block, List<Expr>>();
          foreach (Block exit in LoopsExited(src, target)) {
            List<Expr> predList;
            if (!predsExitingLoop.ContainsKey(exit))
              predList = predsExitingLoop[exit] = new List<Expr>();
            else
              predList = predsExitingLoop[exit];
            predList.Add(part.pred);
          }
          foreach (var pred in predsExitingLoop) {
            PredicateCmd(p, cmdSeq,
                         Cmd.SimpleAssign(Token.NoToken,
                                          Expr.Ident(predMap[pred.Key]),
                                          Expr.Not(pred.Value.Aggregate(Expr.Or))));
          }
        }
      }
    } else if (cmd is ReturnCmd) {
      // Blocks which end in a return will never share a predicate with a block
      // which appears after it.  Furthermore, such a block cannot be part of a
      // loop.  So it is safe to do nothing here.
    } else {
      Console.WriteLine("Unsupported cmd: " + cmd.GetType().ToString());
    }
  }

  Variable FreshPredicate(ref int predCount) {
    var pVar = new LocalVariable(Token.NoToken,
                                 new TypedIdent(Token.NoToken,
                                                "p" + predCount++,
                                                Microsoft.Boogie.Type.Bool));
    impl.LocVars.Add(pVar);
    return pVar;
  }

  void AssignPredicates(Graph<Block> blockGraph,
                        DomRelation<Block> dom,
                        DomRelation<Block> pdom,
                        IEnumerator<Tuple<Block, bool>> i,
                        Variable headPredicate,
                        ref int predCount) {
    var header = i.Current.Item1;
    var regionPreds = new List<Tuple<Block, Variable>>();
    var ownedPreds = new HashSet<Variable>();
    ownedMap[header] = ownedPreds;

    if (headPredicate != null) {
      predMap[header] = headPredicate;
      defMap[header] = headPredicate;
      regionPreds.Add(new Tuple<Block, Variable>(header, headPredicate));
    }

    while (i.MoveNext()) {
      var block = i.Current;
      if (uni != null && uni.IsUniform(impl.Name, block.Item1))
        continue;
      if (block.Item2) {
        if (block.Item1 == header)
          return;
      } else {
        if (blockGraph.Headers.Contains(block.Item1)) {
          parentMap[block.Item1] = header;
          var loopPred = FreshPredicate(ref predCount);
          ownedPreds.Add(loopPred);
          AssignPredicates(blockGraph, dom, pdom, i, loopPred, ref predCount);
        } else {
          bool foundExisting = false;
          foreach (var regionPred in regionPreds) {
            if (dom.DominatedBy(block.Item1, regionPred.Item1) &&
                pdom.DominatedBy(regionPred.Item1, block.Item1)) {
              predMap[block.Item1] = regionPred.Item2;
              foundExisting = true;
              break;
            }
          }
          if (!foundExisting) {
            var condPred = FreshPredicate(ref predCount);
            predMap[block.Item1] = condPred;
            defMap[block.Item1] = condPred;
            ownedPreds.Add(condPred);
            regionPreds.Add(new Tuple<Block, Variable>(block.Item1, condPred));
          }
        }
      }
    }
  }

  void AssignPredicates() {
    DomRelation<Block> dom = blockGraph.DominatorMap;

    Graph<Block> dualGraph = blockGraph.Dual(new Block());
    DomRelation<Block> pdom = dualGraph.DominatorMap;

    var iter = sortedBlocks.GetEnumerator();
    if (!iter.MoveNext()) {
      predMap = defMap = null;
      ownedMap = null;
      return;
    }

    int predCount = 0;
    predMap = new Dictionary<Block, Variable>();
    defMap = new Dictionary<Block, Variable>();
    ownedMap = new Dictionary<Block, HashSet<Variable>>();
    parentMap = new Dictionary<Block, Block>();
    AssignPredicates(blockGraph, dom, pdom, iter,
                     myUseProcedurePredicates ? impl.InParams[0] : null,
                     ref predCount);
  }

  IEnumerable<Block> LoopsExited(Block src, Block dest) {
    var i = sortedBlocks.GetEnumerator();
    while (i.MoveNext()) {
      var b = i.Current;
      if (b.Item1 == src) {
        return LoopsExitedForwardEdge(dest, i);
      } else if (b.Item1 == dest) {
        return LoopsExitedBackEdge(src, i);
      }
    }
    Debug.Assert(false);
    return null;
  }

  private IEnumerable<Block> LoopsExitedBackEdge(Block src, IEnumerator<Tuple<Block, bool>> i) {
    var headsSeen = new HashSet<Block>();
    while (i.MoveNext()) {
      var b = i.Current;
      if (!b.Item2 && blockGraph.Headers.Contains(b.Item1))
        headsSeen.Add(b.Item1);
      else if (b.Item2)
        headsSeen.Remove(b.Item1);
      if (b.Item1 == src)
        return headsSeen;
    }
    Debug.Assert(false);
    return null;
  }

  private IEnumerable<Block> LoopsExitedForwardEdge(Block dest, IEnumerator<Tuple<Block, bool>> i) {
    var headsSeen = new HashSet<Block>();
    while (i.MoveNext()) {
      var b = i.Current;
      if (b.Item1 == dest)
        yield break;
      else if (!b.Item2 && blockGraph.Headers.Contains(b.Item1))
        headsSeen.Add(b.Item1);
      else if (b.Item2 && !headsSeen.Contains(b.Item1))
        yield return b.Item1;
    }
    Debug.Assert(false);
  }

  class PartInfo {
    public PartInfo(Expr p, Block r) { pred = p; realDest = r; }
    public Expr pred;
    public Block realDest;
  }

  Dictionary<Block, PartInfo> BuildPartitionInfo() {
    var partInfo = new Dictionary<Block, PartInfo>();
    foreach (var block in blockGraph.Nodes) {
      if (uni.IsUniform(impl.Name, block))
        continue;

      var parts = block.Cmds.Cast<Cmd>().TakeWhile(
          c => c is AssumeCmd &&
          QKeyValue.FindBoolAttribute(((AssumeCmd)c).Attributes, "partition"));

      Expr pred = null;
      if (parts.Count() > 0) {
        pred = parts.Select(a => ((AssumeCmd)a).Expr).Aggregate(Expr.And);
        block.Cmds =
          new CmdSeq(block.Cmds.Cast<Cmd>().Skip(parts.Count()).ToArray());
      } else {
        continue;
      }

      Block realDest = block;
      if (block.Cmds.Length == 0) {
        var gc = block.TransferCmd as GotoCmd;
        if (gc != null && gc.labelTargets.Length == 1)
          realDest = gc.labelTargets[0];
      }
      partInfo[block] = new PartInfo(pred, realDest);
    }

    return partInfo;
  }

  void PredicateImplementation() {
    blockGraph = prog.ProcessLoops(impl);
    sortedBlocks = blockGraph.LoopyTopSort();

    AssignPredicates();
    partInfo = BuildPartitionInfo();

    if (myUseProcedurePredicates)
      fp = Expr.Ident(impl.InParams[0]);

    var newBlocks = new List<Block>();
    Block prevBlock = null;
    foreach (var n in sortedBlocks) {
      if (predMap.ContainsKey(n.Item1)) {
        var p = predMap[n.Item1];
        var pExpr = Expr.Ident(p);

        if (n.Item2) {
          var backedgeBlock = new Block();
          newBlocks.Add(backedgeBlock);

          backedgeBlock.Label = n.Item1.Label + ".backedge";
          backedgeBlock.Cmds = new CmdSeq(new AssumeCmd(Token.NoToken, pExpr,
            new QKeyValue(Token.NoToken, "backedge", new List<object>(), null)));
          backedgeBlock.TransferCmd = new GotoCmd(Token.NoToken,
                                                  new BlockSeq(n.Item1));

          var tailBlock = new Block();
          newBlocks.Add(tailBlock);

          tailBlock.Label = n.Item1.Label + ".tail";
          tailBlock.Cmds = new CmdSeq(new AssumeCmd(Token.NoToken,
                                               Expr.Not(pExpr)));

          if (uni != null && !uni.IsUniform(impl.Name, n.Item1)) {
            uni.AddNonUniform(impl.Name, backedgeBlock);
            uni.AddNonUniform(impl.Name, tailBlock);
          }

          if (prevBlock != null)
            prevBlock.TransferCmd = new GotoCmd(Token.NoToken,
                                          new BlockSeq(backedgeBlock, tailBlock));
          prevBlock = tailBlock;
        } else {
          PredicateBlock(pExpr, n.Item1, newBlocks, ref prevBlock);
        }
      } else {
        if (!n.Item2) {
          PredicateBlock(null, n.Item1, newBlocks, ref prevBlock);
        }
      }
    }

    if (prevBlock != null)
      prevBlock.TransferCmd = new ReturnCmd(Token.NoToken);

    impl.Blocks = newBlocks;
  }

  private void PredicateBlock(Expr pExpr, Block block, List<Block> newBlocks, ref Block prevBlock) {
    var firstBlock = block;

    var oldCmdSeq = block.Cmds;
    block.Cmds = new CmdSeq();
    newBlocks.Add(block);
    if (prevBlock != null) {
      prevBlock.TransferCmd = new GotoCmd(Token.NoToken, new BlockSeq(block));
    }

    if (parentMap.ContainsKey(block)) {
      var parent = parentMap[block];
      if (predMap.ContainsKey(parent)) {
        var parentPred = predMap[parent];
        if (parentPred != null) {
          block.Cmds.Add(new AssertCmd(Token.NoToken,
                                          pExpr != null ? (Expr)Expr.Imp(pExpr, Expr.Ident(parentPred))
                                                        : Expr.Ident(parentPred)));
        }
      }
    }

    var transferCmd = block.TransferCmd;
    foreach (Cmd cmd in oldCmdSeq)
      PredicateCmd(pExpr, newBlocks, block, cmd, out block);

    if (ownedMap.ContainsKey(firstBlock)) {
      var owned = ownedMap[firstBlock];
      foreach (var v in owned)
        block.Cmds.Add(Cmd.SimpleAssign(Token.NoToken, Expr.Ident(v), Expr.False));
    }

    bool hasPredicatedRegion;
    PredicateTransferCmd(pExpr, block, block.Cmds, transferCmd, out hasPredicatedRegion);

    if (hasPredicatedRegion)
      prevBlock = block;
    else
      prevBlock = null;

    doneBlocks.Add(block);
  }

  private Expr CreateIfFPThenElse(Expr then, Expr eElse) {
    if (myUseProcedurePredicates) {
      return new NAryExpr(Token.NoToken,
                 new IfThenElse(Token.NoToken),
                 new ExprSeq(fp, then, eElse));
    } else {
      return then;
    }
  }

  public static void Predicate(Program p,
                               Func<Procedure, bool> useProcedurePredicates = null,
                               UniformityAnalyser uni = null) {
    useProcedurePredicates = useProcedurePredicates ?? (proc => false);
    if (uni != null) {
      var oldUPP = useProcedurePredicates;
      useProcedurePredicates = proc => oldUPP(proc) && !uni.IsUniform(proc.Name);
    }

    foreach (var decl in p.TopLevelDeclarations.ToList()) {
      if (decl is Procedure || decl is Implementation) {
        var proc = decl as Procedure;
        Implementation impl = null;
        if (proc == null) {
          impl = (Implementation)decl;
          proc = impl.Proc;
        }

        bool upp = useProcedurePredicates(proc);
        if (upp) {
          var dwf = (DeclWithFormals)decl;
          var fpVar = new Formal(Token.NoToken,
                                 new TypedIdent(Token.NoToken, "_P",
                                                Microsoft.Boogie.Type.Bool),
                                 /*incoming=*/true);
          dwf.InParams = new VariableSeq(
            (new Variable[] {fpVar}.Concat(dwf.InParams.Cast<Variable>()))
              .ToArray());

          if (impl == null) {
            var newRequires = new RequiresSeq();
            foreach (Requires r in proc.Requires) {
              newRequires.Add(new Requires(r.Free,
                new EnabledReplacementVisitor(new IdentifierExpr(Token.NoToken, fpVar)).VisitExpr(r.Condition)));
            }
            var newEnsures = new EnsuresSeq();
            foreach (Ensures e in proc.Ensures) {
              newEnsures.Add(new Ensures(e.Free,
                new EnabledReplacementVisitor(new IdentifierExpr(Token.NoToken, fpVar)).VisitExpr(e.Condition)));
            }
          }
        }

        if (impl != null) {
          try {
            new SmartBlockPredicator(p, impl, useProcedurePredicates, uni).PredicateImplementation();
          } catch (Program.IrreducibleLoopException) { }
        }
      }
    }
  }

  public static void Predicate(Program p, Implementation impl) {
    try {
      new SmartBlockPredicator(p, impl, proc => false, null).PredicateImplementation();
    }
    catch (Program.IrreducibleLoopException) { }
  }

}

}