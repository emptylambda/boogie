procedure {:checksum "0"} M();

implementation {:id "M"} {:checksum "2"} M()
{
    if (*)
    {
        assert 1 == 1;
    }
    else
    {
        assert 2 != 2;  // error
    }

    assert 3 != 3;  // error
}
