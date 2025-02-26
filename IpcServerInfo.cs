using System;
using System.Runtime.InteropServices;

namespace MemoryMappedFileIPC
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IpcServerInfo {
        public long timeOfLastUpdate;
        public string baseKey;
        public string guid;
        public int processId;
        public IpcUtils.ConnectionStatus connectionStatus;
    }
}