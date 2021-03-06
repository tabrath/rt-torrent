﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;

namespace Torrent.Client
{
    public delegate void DiskIOReadCallback(bool success, int read, byte[] data, object state);

    public delegate void DiskIOWriteCallback(bool success, int written, object state);

    internal static class DiskIO
    {
        private const int MaxPendingOps = 2500;
        private const int MinPendingOps = 1000;

        private static readonly ConcurrentQueue<DiskIOReadState> ReadQueue;
        private static readonly ConcurrentQueue<DiskIOWriteState> WriteQueue;

        private static readonly Cache<DiskIOReadState> ReadCache;
        private static readonly Cache<DiskIOWriteState> WriteCache;

        private static readonly AutoResetEvent IOHandle;

        static DiskIO()
        {
            ReadQueue = new ConcurrentQueue<DiskIOReadState>();
            WriteQueue = new ConcurrentQueue<DiskIOWriteState>();
            ReadCache = new Cache<DiskIOReadState>();
            WriteCache = new Cache<DiskIOWriteState>();
            IOHandle = new AutoResetEvent(false);
            StartDiskThread();
        }


        public static void QueueRead(Stream stream, byte[] buffer, int bufferOffset, long streamOffset, long length,
                                     DiskIOReadCallback callback, object state)
        {
            while (ReadQueue.Count > MaxPendingOps) 
                Thread.Sleep(10);

            DiskIOReadState readData = ReadCache.Get().Init(stream, buffer, bufferOffset, streamOffset, length, callback,
                                                            state);
            ReadQueue.Enqueue(readData);
            if (ReadQueue.Count > MinPendingOps) IOHandle.Set();
        }

        public static void QueueWrite(Stream stream, byte[] data, long fileOffset, int dataOffset, long length, DiskIOWriteCallback callback,
                                      object state)
        {
            while(WriteQueue.Count > MaxPendingOps) 
                Thread.Sleep(10);

            DiskIOWriteState writeData = WriteCache.Get().Init(stream, data, fileOffset, dataOffset, length, callback, state);
            WriteQueue.Enqueue(writeData);
            if(WriteQueue.Count>MinPendingOps) IOHandle.Set();
        }

        private static void StartDiskThread()
        {
            var diskThread = new Thread(DiskLoop);
            diskThread.IsBackground = true;
            diskThread.Start();
        }

        private static void DiskLoop()
        {
            while (true)
            {
                try
                {
                    IOHandle.WaitOne(200);
                    bool write = true, read = true;
                    var readList = new List<DiskIOReadState>();
                    var writeList = new List<DiskIOWriteState>();
                    while (write)
                    {
                        DiskIOWriteState result;
                        write = WriteQueue.TryDequeue(out result);
                        if(write) writeList.Add(result);
                    }
                    while (read)
                    {
                        DiskIOReadState result;
                        read = ReadQueue.TryDequeue(out result);
                        if (read) readList.Add(result);
                    }

                    foreach(var readState in readList)
                    {
                        Read(readState);
                    }

                    foreach (var writeState in writeList)
                    {
                        Write(writeState);
                    }
                }
                catch(Exception e)
                {
                    Trace.WriteLine(e);
                }
            }
        }

        private static void Write(DiskIOWriteState state)
        {
            try
            {
                lock (state.Data)
                {
                    if (!state.Stream.CanWrite) throw new TorrentException("Stream unwritable.");
                    
                    state.Stream.Seek(state.FileOffset, SeekOrigin.Begin);
                    state.Stream.Write(state.Data, state.DataOffset, (int)state.Length);
                    state.Callback(true, (int)state.Length, state.State);
                    WriteCache.Put(state);
                }
            }
            catch (Exception)
            {
                state.Callback(false, 0, state.State);
                WriteCache.Put(state);
            }
        }

        private static void Read(DiskIOReadState state)
        {
            try
            {
                lock (state.Buffer)
                {
                    if (!state.Stream.CanRead) throw new TorrentException("Stream unreadable.");
                    state.Stream.Seek(state.StreamOffset, SeekOrigin.Begin);
                    int read = state.Stream.Read(state.Buffer, state.BufferOffset, (int)state.Length);
                    if(read != state.Length) 
                        state.Callback(false, read, null, state.State);
                    else state.Callback(true, read, state.Buffer, state.State);
                    ReadCache.Put(state);
                }
            }
            catch (Exception)
            {
                state.Callback(false, 0, null, state.State);
                ReadCache.Put(state);
            }
        }

        #region Nested type: DiskIOReadState

        private class DiskIOReadState : ICacheable
        {
            public Stream Stream { get; private set; }
            public byte[] Buffer { get; private set; }
            public int BufferOffset { get; private set; }
            public long StreamOffset { get; private set; }
            public long Length { get; private set; }
            public DiskIOReadCallback Callback { get; private set; }
            public object State { get; private set; }

            #region ICacheable Members

            public ICacheable Init()
            {
                return Init(null, null, 0, 0, 0, null, null);
            }

            #endregion

            public DiskIOReadState Init(Stream stream, byte[] buffer, int bufferOffset, long streamOffset, long length,
                                        DiskIOReadCallback callback, object state)
            {
                Stream = stream;
                BufferOffset = bufferOffset;
                StreamOffset = streamOffset;
                Length = length;
                Callback = callback;
                State = state;
                Buffer = buffer;
                return this;
            }
        }

        #endregion

        #region Nested type: DiskIOWriteState

        private class DiskIOWriteState : ICacheable
        {
            public Stream Stream { get; private set; }
            public byte[] Data { get; private set; }
            public long FileOffset { get; private set; }
            public int DataOffset { get; private set; }
            public long Length { get; private set; }
            public DiskIOWriteCallback Callback { get; private set; }
            public object State { get; private set; }

            #region ICacheable Members

            public ICacheable Init()
            {
                return Init(null, null, 0, 0, 0, null, null);
            }

            #endregion

            public DiskIOWriteState Init(Stream stream, byte[] data, long fileOffset, int dataOffset, long length,
                                         DiskIOWriteCallback callback, object state)
            {
                Stream = stream;
                FileOffset = fileOffset;
                DataOffset = dataOffset;
                Length = length;
                Data = data;
                Callback = callback;
                State = state;
                return this;
            }
        }

        #endregion
    }
}