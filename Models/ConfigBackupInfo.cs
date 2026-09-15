using System;
using System.IO;

namespace singC.Models;

public sealed class ConfigBackupInfo
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public DateTime LastWriteTime { get; }
    public string DisplayText => $"{LastWriteTime:yyyy-MM-dd HH:mm:ss}  {FileName}";

    public ConfigBackupInfo(string filePath)
    {
        FilePath = filePath;
        LastWriteTime = File.Exists(filePath) ? File.GetLastWriteTime(filePath) : DateTime.MinValue;
    }
}
