﻿using System;
using LibHac.Fs;
using LibHac.Sf;

namespace LibHac.FsSrv.Sf;

public interface ISaveDataImporter : IDisposable
{
    public Result GetSaveDataInfo(out SaveDataInfo outInfo);
    public Result GetRestSize(out ulong outRemainingSize);
    public Result Push(InBuffer buffer);
    // Can't name the method "Finalize" because it's basically a reserved method in .NET
    public Result FinalizeImport();
}