using System;

namespace MemoryMappedFileIPC
{ 
    public struct IpcServerInfo {
        public long timeOfLastUpdate;
        public string baseKey;
        public string guid;
        public int processId;
        public IpcUtils.ConnectionStatus connectionStatus;
    }
}