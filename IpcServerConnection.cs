using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryMappedFileIPC
{
    public class IpcServerConnection : IDisposable
    {
        public static Guid MakeUniqueGuid(string serverDirectory) {
            Guid guid = Guid.NewGuid();
            while (File.Exists(IpcUtils.GuidToConnectionPath(guid, serverDirectory))) {
                guid = Guid.NewGuid();
            }
            return guid;
        }

        public string baseKey;
        public int millisBetweenPing;
        public Guid guid;
        public int processId;

        CancellationTokenSource parentStopToken;
        CancellationTokenSource selfStopTokenSource;
        CancellationTokenSource stopToken;

        public string GetServerKey() {
            return IpcUtils.GetServerKey(baseKey, guid);
        }

        public volatile IpcUtils.ConnectionStatus connectionStatus;

        public delegate void RecievedBytesCallback(byte[][] bytes);
        public event RecievedBytesCallback OnRecievedBytes;

        Thread dataThread;
        Thread pingThread;
        Thread writeStatusThread;
        string serverDirectory;
        IpcUtils.DebugLogType DebugLog;
        public IpcServerConnection(string baseKey, string serverDirectory, int millisBetweenPing, int processId,
            CancellationTokenSource stopToken, IpcUtils.DebugLogType DebugLog=null)
        {
            this.DebugLog = DebugLog;
            if (DebugLog == null)
            {
                this.DebugLog = (x) => { };
            }
            this.baseKey = baseKey;
            this.millisBetweenPing = millisBetweenPing;
            this.processId = processId;
            this.parentStopToken = stopToken;
            this.serverDirectory = serverDirectory;
            this.selfStopTokenSource = new CancellationTokenSource();
            this.stopToken = CancellationTokenSource.CreateLinkedTokenSource(this.selfStopTokenSource.Token, this.parentStopToken.Token);
            this.connectionStatus = IpcUtils.ConnectionStatus.WaitingForConnection;
            guid = MakeUniqueGuid(serverDirectory);
        }



        public void Init() {
            // recieve data
            dataThread = new Thread(() =>
            {
                string id = GetServerKey() + "data";
                try
                {
                    DebugLog("Creating server with key " + id);

                    // PipeOptions.Asynchronous is very important!! Or WaitForConnectionAsync won't stop when stopToken is canceled
                    // actually we just implemented our own version of that because it's not available in resonite
                    using (MemoryMappedFileConnection dataServer = new MemoryMappedFileConnection(id, IpcUtils.BUFFER_SIZE, isWriter: false))
                    {
                        dataServer.WaitForConnection(stopToken.Token);

                        connectionStatus = IpcUtils.ConnectionStatus.Connected;

                        while (!stopToken.IsCancellationRequested)
                        {
                            byte[][] bytes = ReadBytes(dataServer, stopToken);
                            OnRecievedBytes?.Invoke(bytes);
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugLog("Got exception in data thread of server, disconnecting " + e.GetType() + " " + e.Message + " " + e.StackTrace);
                }
                finally
                {
                    selfStopTokenSource.Cancel();
                    DebugLog("Terminating reading thread of server connected to " + GetServerKey());
                }
            });
            
            // recieve keepalive ping
            pingThread = new Thread(() =>
            {
                string id = GetServerKey() + "ping";
                try
                {
                    DebugLog("Connecting to " + id);
                    using (SharedEventWaitHandle pingClientHandle = new SharedEventWaitHandle(id + "clientHandle", false, false))
                    {
                        using (SharedEventWaitHandle pingServerHandle = new SharedEventWaitHandle(id + "serverHandle", false, false))
                        {
                            pingClientHandle.WaitOrCancel(stopToken.Token); // wait for initial connection
                            DebugLog("Connected ping thread to: " + id);
                            while (!stopToken.IsCancellationRequested)
                            {
                                pingServerHandle.waitHandle.Set();
                                pingClientHandle.WaitOrCancel(stopToken.Token, this.millisBetweenPing * 2);
                                DebugLog("Got ping from: " + id);
                                Thread.Sleep(millisBetweenPing); // it would be nice to do cancellable sleep but that risks taking longer if async gets full
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugLog("Got exception in ping thread of server, disconnecting " + e.GetType() + " " + e.Message + " " + e.StackTrace);
                }
                finally
                {
                    this.connectionStatus = IpcUtils.ConnectionStatus.Terminated;
                    selfStopTokenSource.Cancel();
                    DebugLog("Terminating ping thread of server connected to " + id);
                }
            });

            // server status thread
            writeStatusThread = new Thread (() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        WriteServerStatus(stopToken.Token);
                        // this will immediately return if stopToken canceled
                        Task.Delay(millisBetweenPing, stopToken.Token).GetAwaiter().GetResult();
                    }
                }
                catch (TaskCanceledException)
                {

                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    DebugLog("Got exception in status thread of server, disconnecting " + ex.GetType() + " " + ex.Message + " " + ex.StackTrace);
                }
                finally
                {
                    // remove our file since we are closed
                    IpcUtils.SafeDeleteFile(IpcUtils.GuidToConnectionPath(this.guid, this.serverDirectory), this.stopToken.Token);
                    DebugLog("Terminating write status thread of server connected to " + GetServerKey());
                }
            });

            dataThread.Start();
            pingThread.Start();
            writeStatusThread.Start();
        }

        public void Dispose() {
            if (selfStopTokenSource != null)
            {
                selfStopTokenSource.Cancel();
            }
            if (dataThread != null)
            {
                dataThread.Join();
                dataThread = null;
            }
            if (pingThread != null)
            {
                pingThread.Join();
                pingThread = null;
            }
            if (writeStatusThread != null)
            {
                writeStatusThread.Join();
                writeStatusThread = null;
            }
            if (stopToken != null)
            {
                stopToken.Dispose();
                stopToken = null;
            }
            if (selfStopTokenSource != null)
            {
                selfStopTokenSource.Dispose();
                selfStopTokenSource = null;
            }
            DebugLog("Finished disposing server " + GetServerKey());
        }

        public byte[][] ReadBytes(MemoryMappedFileConnection connection, CancellationTokenSource readStopToken, int millisTimeout=-1)
        {
            byte[] numByteArraysBytes = connection.ReadData(readStopToken.Token, millisTimeout);
            int numByteArrays = BitConverter.ToInt32(numByteArraysBytes, 0);
            byte[][] bytes = new byte[numByteArrays][];
            for (int i = 0; i < numByteArrays; i++)
            {
                bytes[i] = connection.ReadData(readStopToken.Token, millisTimeout);
            }
            return bytes;
        }

        public void WriteServerStatus(CancellationToken stopToken) {
            IpcServerInfo serverInfo = new IpcServerInfo() {
                timeOfLastUpdate = IpcUtils.TimeMillis(),
                guid = this.guid.ToString(),
                processId = this.processId,
                connectionStatus = this.connectionStatus,
                baseKey = this.baseKey
            };

            IpcUtils.SafeWriteAllBytes(
                IpcUtils.GuidToConnectionPath(this.guid, this.serverDirectory),
                SerializationUtils.EncodeObject(serverInfo),
                stopToken
            );
        }
    }
}