using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryMappedFileIPC
{
    public class SharedEventWaitHandle : IDisposable
    {
        public EventWaitHandle waitHandle;
        public SharedEventWaitHandle(string name, bool initialState, bool openExisting)
        {
            // need to add security to use shared event wait handle
            //var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            //var rule = new EventWaitHandleAccessRule(users, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
            //                          AccessControlType.Allow);
            //var security = new EventWaitHandleSecurity();
            //security.AddAccessRule(rule);
            if (openExisting)
            {
                waitHandle = EventWaitHandle.OpenExisting("Global\\" + name);
            }
            else
            {
                waitHandle = new EventWaitHandle(initialState, EventResetMode.AutoReset, "Global\\" + name, out bool created);
                if (!created)
                {
                    throw new ArgumentException("failed to create event handle " + name);
                }
            }
        }

        public void Dispose()
        {
            if (waitHandle != null)
            {
                waitHandle.Dispose();
                waitHandle = null;
            }
        }

        public void WaitOrCancel(CancellationToken cancelToken, int timeoutMillis = -1)
        {
            WaitOrCancel(waitHandle, cancelToken, timeoutMillis);
        }

        public static void WaitOrCancel(WaitHandle waitHandle, CancellationToken cancelToken, int timeoutMillis = -1)
        {
            if (timeoutMillis >= 0)
            {
                using (CancellationTokenSource timeoutToken = new CancellationTokenSource(timeoutMillis))
                {
                    WaitHandle.WaitAny(new WaitHandle[] { waitHandle, cancelToken.WaitHandle, timeoutToken.Token.WaitHandle });
                    if (timeoutToken.IsCancellationRequested)
                    {
                        throw new TimeoutException();
                    }
                }
            }
            else
            {
                WaitHandle.WaitAny(new WaitHandle[] { waitHandle, cancelToken.WaitHandle });
            }
            if (cancelToken.IsCancellationRequested)
            {
                throw new TaskCanceledException();
            }
        }

    }
    public class MemoryMappedFileConnection : IDisposable
    {
        MemoryMappedFile file;
        MemoryMappedViewAccessor accessor;
        SharedEventWaitHandle readyForRead;
        SharedEventWaitHandle finishedRead;
        SharedEventWaitHandle connected;
        SharedEventWaitHandle readyForConnection;

        const int STATUS_POSITION = 0; // byte
        const int TOTAL_LENGTH_POSITION = 1; // ulong
        const int CHUNK_LENGTH_POSITION = 9; // uint
        const byte ACKNOWLEDGE_POSITION = 13; // byte
        const int DATA_POSITION = 14; // rest of data, as byte array

        // no data availble
        // readonly byte NO_DATA = 1;
        // some of the data available (chunked)
        readonly byte PARTIAL_DATA = 2;
        // last chunk of data (or only chunk of data)
        readonly byte FINAL_DATA = 3;

        readonly byte NO_ACKNOWLEDGE = 1;
        readonly byte ACKNOWLEDGED = 2;

        readonly int bufferSize;

        // doesn't need to be shared since only the server uses it
        AutoResetEvent readyToWrite = new AutoResetEvent(false);

        readonly bool isWriter;

        public MemoryMappedFileConnection(string id, int bufferSize, bool isWriter)
        {
            readyForRead = new SharedEventWaitHandle(id + "readyForRead", false, isWriter);
            finishedRead = new SharedEventWaitHandle(id + "finishedRead", false, isWriter);
            connected = new SharedEventWaitHandle(id + "connected", false, isWriter);
            readyForConnection = new SharedEventWaitHandle(id + "readyForConnection", false, isWriter);
            file = MemoryMappedFile.CreateOrOpen(id, (long)bufferSize, MemoryMappedFileAccess.ReadWrite);
            accessor = file.CreateViewAccessor(0, bufferSize);
            if (isWriter)
            {
                // first byte contains status, put no data
                accessor.Write(STATUS_POSITION, (byte)1);
            }
            this.isWriter = isWriter;
            // next eight bytes contain current message total length
            // next four bytes contain current chunk length
            // rest of bytes contain message
            this.bufferSize = bufferSize - DATA_POSITION;
            readyToWrite.Set();
        }

        public void WaitForConnection(CancellationToken cancelToken, int timeoutMillis = -1)
        {
            if (isWriter)
            {
                throw new ArgumentOutOfRangeException("Only readers can wait for connection, use Connect instead");
            }
            // we need a readyForConnection handle so only one will connect
            // (this prevents multiple connecting to this which breaks assumptions we have)
            readyForConnection.waitHandle.Set();
            connected.WaitOrCancel(cancelToken, timeoutMillis);
        }

        public void Connect(CancellationToken cancelToken, int timeoutMillis = -1)
        {
            if (!isWriter)
            {
                throw new ArgumentOutOfRangeException("Only writers can call connect, use WaitForConnection instead");
            }
            readyForConnection.WaitOrCancel(cancelToken, timeoutMillis);
            connected.waitHandle.Set();
        }


        public bool IsPartialChunkOfData()
        {
            return accessor.ReadByte(STATUS_POSITION) == PARTIAL_DATA;
        }

        int ReadChunk(byte[] outBytes, int offset)
        {
            int length = accessor.ReadInt32(CHUNK_LENGTH_POSITION);
            // also set the ADKNOWLEDGED byte so the client knows we did something
            accessor.Write(ACKNOWLEDGE_POSITION, ACKNOWLEDGED);
            accessor.ReadArray<byte>(DATA_POSITION, outBytes, offset, length);
            return length;
        }


        public byte[] ReadData(CancellationToken cancelToken, int timeoutMillis = -1)
        {
            if (isWriter)
            {
                throw new ArgumentOutOfRangeException("Only readers can ReadData");
            }
            readyForRead.WaitOrCancel(cancelToken, timeoutMillis);

            ulong totalLength = accessor.ReadUInt64(TOTAL_LENGTH_POSITION);

            byte[] resultBytes = new byte[totalLength];

            int offset = 0;
            while (IsPartialChunkOfData())
            {
                offset += ReadChunk(resultBytes, offset);
                // signal we are done reading
                finishedRead.waitHandle.Set();
                // wait until they have inserted the next data
                readyForRead.WaitOrCancel(cancelToken, timeoutMillis);
            }
            ReadChunk(resultBytes, offset);
            finishedRead.waitHandle.Set();
            return resultBytes;
        }

        void WriteDataChunk(byte[] data, int offset, int chunkLen)
        {
            accessor.WriteArray<byte>(DATA_POSITION, data, offset, chunkLen);
        }

        public void WriteData(byte[] data, int offset, int len, CancellationToken cancelToken, int timeoutMillis = -1)
        {
            if (!isWriter)
            {
                throw new ArgumentOutOfRangeException("Only writers can WriteData");
            }
            SharedEventWaitHandle.WaitOrCancel(readyToWrite, cancelToken, timeoutMillis);
            accessor.Write(TOTAL_LENGTH_POSITION, (ulong)len);
            for (int chunkStart = 0; chunkStart < len; chunkStart += this.bufferSize)
            {
                int remaining = len - chunkStart;
                int chunkLen = Math.Min(this.bufferSize, remaining);
                accessor.Write(CHUNK_LENGTH_POSITION, chunkLen);
                WriteDataChunk(data, offset + chunkStart, chunkLen);
                bool partialChunk = (chunkStart + this.bufferSize) < len;
                accessor.Write(STATUS_POSITION, partialChunk ? PARTIAL_DATA : FINAL_DATA);
                // wait for finishedRead should be sufficient, however, if the server disposes of the events
                // then on some OS they get stuck in a perpertual "Set" state
                // so we need to see that they change the acknowledge bit as well
                accessor.Write(ACKNOWLEDGE_POSITION, NO_ACKNOWLEDGE);
                // it can get stuck on ACKNOWLEDGED if server disconnects, if so, bail (not sure that is true, actually, but just in case)
                if (accessor.ReadByte(ACKNOWLEDGE_POSITION) != NO_ACKNOWLEDGE)
                {
                    throw new IpcUtils.DisconnectedException();
                }

                readyForRead.waitHandle.Set();
                finishedRead.WaitOrCancel(cancelToken, timeoutMillis);
                // sometimes the ACHKNOWLEDGE takes longer to sync than the wait handles,
                // try a few times
                
                bool acknowledged = false;
                int ACKNOWLEDGE_RETRIES = 2;
                int ACKNOWLEDGE_WAIT_MILLIS = 100; // can be pretty small, its very fast
                for (int i = 0; i < ACKNOWLEDGE_RETRIES; i++)
                {
                    if (accessor.ReadByte(ACKNOWLEDGE_POSITION) == ACKNOWLEDGED)
                    {
                        acknowledged = true;
                        break;
                    }
                    else
                    {
                        Task.Delay(ACKNOWLEDGE_WAIT_MILLIS, cancelToken).GetAwaiter().GetResult();
                    }
                }
                if (!acknowledged)
                {
                    throw new IpcUtils.DisconnectedException();
                }
            }
            // we are done, a new task can write now
            readyToWrite.Set();
        }

        public void Dispose()
        {
            // need these null checks becauase a partial init can happen and if we don't have them
            // then we'll get an error and fail to clean up everything
            if (accessor != null)
            {
                accessor.Dispose();
                accessor = null;
            }
            if (file != null)
            {
                file.Dispose();
                file = null;
            }
            if (readyToWrite != null)
            {
                readyToWrite.Dispose();
                readyToWrite = null;
            }
            if (readyForRead != null)
            {
                readyForRead.Dispose();
                readyForRead = null;
            }
            if (finishedRead != null)
            {
                finishedRead.Dispose();
                finishedRead = null;
            }
            if (connected != null)
            {
                connected.Dispose();
                connected = null;
            }
            if (readyForConnection == null)
            {
                readyForConnection.Dispose();
                readyForConnection = null;
            }
        }
    }
}
