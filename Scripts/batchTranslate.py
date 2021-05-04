#!/usr/bin/python3
# usage: python batchTranslate.py directory

import os, sys



if __name__ == "__main__":
    dirs = os.listdir(sys.argv[1])
    for file in dirs:
        p = os.path.join(sys.argv[1], file)
        if(os.path.isdir(p)):
            print(p + ":")
            for f in os.listdir(p):
                if(os.path.isfile(os.path.join(p,f)) and os.path.splitext(f)[1] == ".bpl"):
                    sourceBPL = os.path.join(p, f)
                    targetDir = "../BoogieCollection/SMTs/DAFNY3/"+ file
                    os.makedirs(targetDir, exist_ok=True)
                    targetSMT2 = os.path.join(targetDir, os.path.splitext(f)[0] + ".smt2")
                    command = f'/home/jeff/Dev/Jeff_Boogie/Scripts/boogie -tryModFix {sourceBPL} -proverLog:{targetSMT2} -timeLimit:1'
                    os.system(command)
                    #print("\t/" + f)


