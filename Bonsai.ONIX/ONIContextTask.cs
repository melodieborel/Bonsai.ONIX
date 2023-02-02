﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bonsai.ONIX
{
    public class ONIContextTask : IDisposable
    {
        private oni.Context ctx;

        /// <summary>
        /// Maximum amount of frames the reading queue will hold. If the queue fills or the read
        /// thread is not performant enough to fill it faster than data is produced, frame reading
        /// will throttle, filling host memory instead of userspace memory.
        /// </summary>
        private const int MaxQueuedFrames = 2_000_000;

        /// <summary>
        /// Timeout in ms for queue reads. This should not be critical as the read operation will
        /// cancel if the task is stopped
        /// </summary>
        private const int QueueTimeoutMilliseconds = 200;

        // NB: Decouple OnNext() form hadware reads
        private Task readFrames;
        private Task distributeFrames;
        private BlockingCollection<oni.Frame> FrameQueue;
        private CancellationTokenSource CollectFramesTokenSource;
        private CancellationToken CollectFramesToken;
        internal event EventHandler<FrameReceivedEventArgs> FrameReceived;

        public static readonly string DefaultDriver = "riffa";
        public static readonly int DefaultIndex = 0;

        // TODO: These work for RIFFA implementation, but potentially not others!!
        private readonly object readLock = new object();
        private readonly object writeLock = new object();
        private readonly object regLock = new object();

        private bool running = false;
        private readonly object runLock = new object();

        private readonly string contextDriver = DefaultDriver;
        private readonly int contextIndex = DefaultIndex;

        public ONIContextTask(string driver, int index)
        {
            contextDriver = driver;
            contextIndex = index;
            lock (readLock)
                lock (writeLock)
                    lock (regLock)
                    {
                        Initialize();
                    }
        }

        private void Initialize()
        {
            ctx = new oni.Context(contextDriver, contextIndex);
            SystemClockHz = ctx.SystemClockHz;
            AcquisitionClockHz = ctx.AcquisitionClockHz;
            MaxReadFrameSize = ctx.MaxReadFrameSize;
            MaxWriteFrameSize = ctx.MaxWriteFrameSize;
            DeviceTable = ctx.DeviceTable;
        }

        public void Reset()
        {
            lock (runLock)
            {
                Stop();
                lock (readLock)
                    lock (writeLock)
                        lock (regLock)
                        {
                            ctx?.Dispose();
                            Initialize();
                        }
            }
        }

        public uint SystemClockHz { get; private set; }
        public uint AcquisitionClockHz { get; private set; }
        public uint MaxReadFrameSize { get; private set; }
        public uint MaxWriteFrameSize { get; private set; }
        public Dictionary<uint, oni.Device> DeviceTable { get; private set; }

        internal void Start()
        {
            lock (runLock)
            {
                if (running) return;

                // NB: Stuff related to sync mode is 100% ONIX, not ONI, so long term another place
                // to do this separation might be needed
                int addr = ctx.HardwareAddress;
                int mode = (addr & 0x00FF0000) >> 16;
                if (mode == 0) // Standalone mode
                {
                    ctx.Start(true);
                }
                else // If synchronized mode, reset counter independently
                {
                    ctx.ResetFrameClock();
                    ctx.Start(false);
                }

                CollectFramesTokenSource = new CancellationTokenSource();
                CollectFramesToken = CollectFramesTokenSource.Token;

                FrameQueue = new BlockingCollection<oni.Frame>(MaxQueuedFrames);

                readFrames = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (!CollectFramesToken.IsCancellationRequested)
                        {
                            // NB: This is a blocking call and there is no safe way to terminate it
                            // other than ending the process. For this reason, it is the job of the 
                            // hardware to provide enough data (e.g. through a HeartbeatDevice") for
                            // this call to return.
                            oni.Frame frame = ReadFrame();
                            FrameQueue.Add(frame, CollectFramesToken);

                        }
                    } catch (OperationCanceledException)
                    {
#if DEBUG
                        // NB: If FrameQueue.Add has not been called, frame has ref count 0 when it exits
                        // while loop context and will be disposed.
                        Console.WriteLine("Frame collection task has been cancelled by " + this.GetType());
#endif
                    };
                },
                CollectFramesToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

                distributeFrames = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (!CollectFramesToken.IsCancellationRequested)
                        {
                            if (FrameQueue.TryTake(out oni.Frame frame, QueueTimeoutMilliseconds, CollectFramesToken))
                            {
                                OnFrameReceived(new FrameReceivedEventArgs(frame));
                            }
                        }
                    } catch (OperationCanceledException)
                    {
#if DEBUG
                        // NB: If the thread stops no frame has been collected
                        Console.WriteLine("Frame distribution task has been cancelled by " + this.GetType());
#endif
                    }
                },
                CollectFramesToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            }

            running = true;
        }

        internal void Stop()
        {
            lock (runLock)
            {
                if (!running) return;
                if ((distributeFrames != null || readFrames != null) && !distributeFrames.IsCanceled)
                {
                    CollectFramesTokenSource.Cancel();
                    Task.WaitAll(new Task[] { distributeFrames, readFrames });
                }
                CollectFramesTokenSource?.Dispose();
                CollectFramesTokenSource = null;

                // Clear queue and free memory
                while (FrameQueue?.Count > 0)
                {
                    oni.Frame frame;
                    frame = FrameQueue.Take();
                    DisposeFrame(frame);
                }
                FrameQueue?.Dispose();
                FrameQueue = null;
                ctx.Stop();
                running = false;
            }
        }

        #region oni.Context delegates
        internal Action<int, int> SetCustomOption => ctx.SetCustomOption;
        internal Func<int, int> GetCustomOption => ctx.GetCustomOption;
        internal Action ResetFrameClock => ctx.ResetFrameClock;

        internal bool Running
        {
            get
            {
                return ctx.Running;
            }
        }

        public int HardwareAddress
        {
            get
            {
                return ctx.HardwareAddress;
            }
            set
            {
                ctx.HardwareAddress = value;
            }
        }

        public int BlockReadSize
        {
            get
            {
                return ctx.BlockReadSize;
            }
            set
            {
                ctx.BlockReadSize = value;
            }
        }

        public int BlockWriteSize
        {
            get
            {
                return ctx.BlockWriteSize;
            }
            set
            {
                ctx.BlockWriteSize = value;
            }
        }

        public int HubState
        {
            get
            {
                return ctx.GetCustomOption((int)oni.ONIXOption.PORTFUNC);
            }
            set
            {
                ctx.SetCustomOption((int)oni.ONIXOption.PORTFUNC, value);
            }
        }

        internal uint ReadRegister(uint deviceIndex, uint registerAddress)
        {
            lock (regLock)
            {
                return ctx.ReadRegister(deviceIndex, registerAddress);
            }
        }

        internal void WriteRegister(uint deviceIndex, uint registerAddress, uint value)
        {
            lock (regLock)
            {
                ctx.WriteRegister(deviceIndex, registerAddress, value);
            }
        }

        public oni.Frame ReadFrame()
        {
            lock (regLock)
            {
                return ctx.ReadFrame();
            }
        }

        public void Write<T>(uint deviceIndex, T data) where T : unmanaged
        {
            lock (writeLock)
            {
                ctx.Write(deviceIndex, data);
            }
        }

        public void Write<T>(uint deviceIndex, T[] data) where T : unmanaged
        {
            lock (writeLock)
            {
                ctx.Write(deviceIndex, data);
            }
        }

        public void Write(uint deviceIndex, IntPtr data, int dataSize)
        {
            lock (writeLock)
            {
                ctx.Write(deviceIndex, data, dataSize);
            }
        }

        public oni.Hub GetHub(uint deviceIndex) { return ctx.GetHub(deviceIndex); }
        #endregion

        private void OnFrameReceived(FrameReceivedEventArgs e)
        {
            FrameReceived?.Invoke(this, e);
            DisposeFrame(e.Frame);
        }

        private static void DisposeFrame(oni.Frame frame)
        {
            frame.Dispose();
        }

        public void Dispose()
        {
            lock (runLock)
            {
                Stop();
                lock (readLock)
                    lock (writeLock)
                        lock (regLock)
                        {
                            ctx?.Dispose();
                        }
            }

            GC.SuppressFinalize(this);
        }
    }
}
