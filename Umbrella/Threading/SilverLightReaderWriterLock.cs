using System;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace nVentive.Umbrella.Threading
{
    /// optimize
    using System;                            // for Basic system types
    using System.Text;                       // for StringBuilder
    using System.Text.RegularExpressions;    // for Regex.Match
    using System.IO;                         // for File, Path 
    using System.Collections.Generic;        // for List<T> Dictionary<T>
    using System.Diagnostics;                // for TraceInformation ...
    using System.Threading;
    using nVentive.Umbrella.Sources;

    /// <summary>
    /// A reader-writer lock implementation that is intended to be simple, yet very
    /// efficient.  In particular only 1 interlocked operation is taken for any lock 
    /// operation (we use spin locks to achieve this).  The spin lock is never held
    /// for more than a few instructions (in particular, we never call event APIs
    /// or in fact any non-trivial API while holding the spin lock).   
    /// 
    /// Currently this ReaderWriterLock does not support recurision, however it is 
    /// not hard to add 
    /// </summary>
    public class ReaderWriterLock
    {
        // Lock specifiation for myLock:  This lock protects exactly the local fields associted
        // instance of MyReaderWriterLock.  It does NOT protect the memory associted with the
        // the events that hang off this lock (eg writeEvent, readEvent upgradeEvent).
        int myLock;

        // Who owns the lock owners > 0 => readers
        // owners = -1 means there is one writer.  Owners must be >= -1.  
        int owners;

        // These variables allow use to avoid Setting events (which is expensive) if we don't have to. 
        uint numWriteWaiters;        // maximum number of threads that can be doing a WaitOne on the writeEvent 
        uint numReadWaiters;         // maximum number of threads that can be doing a WaitOne on the readEvent
        uint numUpgradeWaiters;      // maximum number of threads that can be doing a WaitOne on the upgradeEvent (at most 1). 

        // conditions we wait on. 
        EventWaitHandle writeEvent;    // threads waiting to aquire a write lock go here.
        EventWaitHandle readEvent;     // threads waiting to aquire a read lock go here (will be released in bulk)
        EventWaitHandle upgradeEvent;  // thread waiting to upgrade a read lock to a write lock go here (at most one)

        [ThreadStatic]
        private ThreadLocalSource<int> _writerOwner = new ThreadLocalSource<int>(0); 

        public ReaderWriterLock()
        {
            // All state can start out zeroed. 
        }

        public void AcquireReaderLock(int timeoutMilliseconds)
        {
            EnterMyLock();
            for (; _writerOwner.Value == 0; )
            {
                // We can enter a read lock if there are only read-locks have been given out
                // and a writer is not trying to get in.  
                if (owners >= 0 && numWriteWaiters == 0)
                {
                    // Good case, there is no contention, we are basically done
                    owners++;       // Indicate we have another reader
                    break;
                }

                // Drat, we need to wait.  Mark that we have waiters and wait.  
                if (readEvent == null)      // Create the needed event 
                {
                    LazyCreateEvent(ref readEvent, false);
                    continue;   // since we left the lock, start over. 
                }

                WaitOnEvent(readEvent, ref numReadWaiters, timeoutMilliseconds);
            }
            ExitMyLock();
        }

        public void AcquireWriterLock(int timeoutMilliseconds)
        {
            EnterMyLock();
            for (; _writerOwner.Value == 0; )
            {
                if (owners == 0)
                {
                    // Good case, there is no contention, we are basically done
                    owners = -1;    // indicate we have a writer.
                    break;
                }

                // Drat, we need to wait.  Mark that we have waiters and wait.
                if (writeEvent == null)     // create the needed event.
                {
                    LazyCreateEvent(ref writeEvent, true);
                    continue;   // since we left the lock, start over. 
                }

                WaitOnEvent(writeEvent, ref numWriteWaiters, timeoutMilliseconds);
            }
            ExitMyLock();

            _writerOwner.Value++;
        }

        public object UpgradeToWriterLock(int timeoutMilliseconds)
        {
            EnterMyLock();
            for (; _writerOwner.Value == 0; )
            {
                Debug.Assert(owners > 0, "Upgrading when no reader lock held");
                if (owners == 1)
                {
                    // Good case, there is no contention, we are basically done
                    owners = -1;    // inidicate we have a writer. 
                    break;
                }

                // Drat, we need to wait.  Mark that we have waiters and wait. 
                if (upgradeEvent == null)   // Create the needed event
                {
                    LazyCreateEvent(ref upgradeEvent, false);
                    continue;   // since we left the lock, start over. 
                }

                if (numUpgradeWaiters > 0)
                {
                    ExitMyLock();
                    throw new Exception("UpgradeToWriterLock already in process.  Deadlock!");
                }

                WaitOnEvent(upgradeEvent, ref numUpgradeWaiters, timeoutMilliseconds);
            }
            ExitMyLock();

            _writerOwner.Value++;

            return null;
        }

        public void ReleaseReaderLock()
        {
            EnterMyLock();
            Debug.Assert(owners > 0 && _writerOwner.Value == 0 || _writerOwner.Value != 0, "ReleasingReaderLock: releasing lock and no read lock taken");
            if (_writerOwner.Value == 0)
            {
                --owners;
            }
            ExitAndWakeUpAppropriateWaiters();
        }

        public void ReleaseWriterLock()
        {
            EnterMyLock();
            Debug.Assert(owners == -1, "Calling ReleaseWriterLock when no write lock is held");
            // Debug.Assert(numUpgradeWaiters > 0);
            owners++;
            _writerOwner.Value--;
            ExitAndWakeUpAppropriateWaiters();
        }

        public void DowngradeFromWriterLock(ref object cookie)
        {
            EnterMyLock();
            Debug.Assert(owners == -1 || _writerOwner.Value != 0, "Downgrading when no writer lock held");
            owners = 1;
            _writerOwner.Value--;
            ExitAndWakeUpAppropriateWaiters();
        }

        /// <summary>
        /// A routine for lazily creating a event outside the lock (so if errors
        /// happen they are outside the lock and that we don't do much work
        /// while holding a spin lock).  If all goes well, reenter the lock and
        /// set 'waitEvent' 
        /// </summary>
        private void LazyCreateEvent(ref EventWaitHandle waitEvent, bool makeAutoResetEvent)
        {
            Debug.Assert(MyLockHeld);
            Debug.Assert(waitEvent == null);

            ExitMyLock();
            EventWaitHandle newEvent;
            if (makeAutoResetEvent)
                newEvent = new AutoResetEvent(false);
            else
                newEvent = new ManualResetEvent(false);
            EnterMyLock();
            if (waitEvent == null)          // maybe someone snuck in. 
                waitEvent = newEvent;
        }

        /// <summary>
        /// Waits on 'waitEvent' with a timeout of 'millisceondsTimeout.  
        /// Before the wait 'numWaiters' is incremented and is restored before leaving this routine.
        /// </summary>
        private void WaitOnEvent(EventWaitHandle waitEvent, ref uint numWaiters, int millisecondsTimeout)
        {
            Debug.Assert(MyLockHeld);

            waitEvent.Reset();
            numWaiters++;

            bool waitSuccessful = false;
            ExitMyLock();      // Do the wait outside of any lock 
            try
            {
                if (!waitEvent.WaitOne(TimeSpan.FromMilliseconds(millisecondsTimeout), false))
                    throw new Exception("ReaderWriterLock timeout expired");
                waitSuccessful = true;
            }
            finally
            {
                EnterMyLock();
                --numWaiters;
                if (!waitSuccessful)        // We are going to throw for some reason.  Exit myLock. 
                    ExitMyLock();
            }
        }

        /// <summary>
        /// Determines the appropriate events to set, leaves the locks, and sets the events. 
        /// </summary>
        private void ExitAndWakeUpAppropriateWaiters()
        {
            Debug.Assert(MyLockHeld);

            if (owners == 0 && numWriteWaiters > 0)
            {
                ExitMyLock();      // Exit before signaling to improve efficiency (wakee will need the lock)
                writeEvent.Set();   // release one writer. 
            }
            else if (owners == 1 && numUpgradeWaiters != 0)
            {
                ExitMyLock();          // Exit before signaling to improve efficiency (wakee will need the lock)
                upgradeEvent.Set();     // release all upgraders (however there can be at most one). 
                // two threads upgrading is a guarenteed deadlock, so we throw in that case. 
            }
            else if (owners >= 0 && numReadWaiters != 0)
            {
                ExitMyLock();    // Exit before signaling to improve efficiency (wakee will need the lock)
                readEvent.Set();  // release all readers. 
            }
            else
                ExitMyLock();
        }

        private void EnterMyLock()
        {
            if (Interlocked.CompareExchange(ref myLock, 1, 0) != 0)
                EnterMyLockSpin();
        }

        private void EnterMyLockSpin()
        {
            for (int i = 0; ; i++)
            {
                if (i < 3 && Environment.ProcessorCount > 1)
                    Thread.SpinWait(20);    // Wait a few dozen instructions to let another processor release lock. 
                else
                    Thread.Sleep(0);        // Give up my quantum.  

                if (Interlocked.CompareExchange(ref myLock, 1, 0) == 0)
                    return;
            }
        }
        private void ExitMyLock()
        {
            Debug.Assert(myLock != 0, "Exiting spin lock that is not held");
            myLock = 0;
        }

        private bool MyLockHeld { get { return myLock != 0; } }

    };
}
