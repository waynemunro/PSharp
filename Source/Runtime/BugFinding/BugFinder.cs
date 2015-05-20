﻿//-----------------------------------------------------------------------
// <copyright file="BugFinder.cs" company="Microsoft">
//      Copyright (c) Microsoft Corporation. All rights reserved.
// 
//      THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, 
//      EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED WARRANTIES 
//      OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
// ----------------------------------------------------------------------------------
//      The example companies, organizations, products, domain names,
//      e-mail addresses, logos, people, places, and events depicted
//      herein are fictitious.  No association with any real company,
//      organization, product, domain name, email address, logo, person,
//      places, or events is intended or should be inferred.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.PSharp.IO;

namespace Microsoft.PSharp.BugFinding
{
    /// <summary>
    /// Class implementing the P# bug-finding scheduler.
    /// </summary>
    public class BugFinder
    {
        #region fields

        /// <summary>
        /// The scheduler to be used for bug-finding.
        /// </summary>
        private IScheduler Scheduler;

        /// <summary>
        /// List of active machines to schedule.
        /// </summary>
        private List<Machine> ActiveMachines;

        /// <summary>
        /// Map from machines to their infos.
        /// </summary>
        private Dictionary<Machine, MachineInfo> MachineInfoMap;

        #endregion

        #region public bug-finder methods

        /// <summary>
        /// Constructor.
        /// </summary>
        public BugFinder()
        {
            if (Runtime.Options.BugFindingStrategy == Runtime.BugFindingStrategy.Random)
                this.Scheduler = new RandomScheduler(DateTime.Now.Millisecond);
            else if (Runtime.Options.BugFindingStrategy == Runtime.BugFindingStrategy.DFS)
                this.Scheduler = new DFSScheduler(0);

            this.ActiveMachines = new List<Machine>();
            this.MachineInfoMap = new Dictionary<Machine, MachineInfo>();

            Utilities.WriteSchedule("<ScheduleLog> Configuration: {0}.",
                this.Scheduler.GetDescription());
        }

        /// <summary>
        /// Schedules the next machine to execute.
        /// </summary>
        /// <param name="machine">Machine</param>
        /// <returns>Boolean</returns>
        public void Schedule(Machine machine)
        {
            this.MachineInfoMap[machine].IsActive = false;

            Machine next = null;
            if (!this.Scheduler.TryGetNext(out next, this.ActiveMachines))
            {
                Utilities.WriteSchedule("<ScheduleLog> Schedule explored.",
                    machine, machine.Id);
                this.Close(machine);
                return;
            }

            Utilities.WriteSchedule("<ScheduleLog> Machine {0}({1}) is scheduled.",
                next, next.Id);
            this.MachineInfoMap[next].IsActive = true;

            if (machine.Id != next.Id)
            {
                lock (next)
                {
                    System.Threading.Monitor.PulseAll(next);
                }

                if (!this.MachineInfoMap[machine].IsPaused &&
                    !this.MachineInfoMap[machine].IsHalted)
                {
                    lock (machine)
                    {
                        while (!this.MachineInfoMap[machine].IsActive)
                        {
                            System.Threading.Monitor.Wait(machine);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Notify that the machine has finished its current event handling
        /// loop.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyHandlerStarted(Machine machine)
        {
            this.TryAddActiveMachine(machine);

            if (this.ActiveMachines.Count == 1)
            {
                this.MachineInfoMap[machine].IsActive = true;
            }
            
            lock (machine)
            {
                while (!this.MachineInfoMap[machine].IsActive)
                {
                    System.Threading.Monitor.Wait(machine);
                }
            }
        }

        /// <summary>
        /// Notify that the machine has paused its current event
        /// handling loop, because its event queue is empty.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyHandlerPaused(Machine machine)
        {
            Utilities.WriteSchedule("<ScheduleLog> Machine {0}({1}) paused.",
                machine, machine.Id);

            this.MachineInfoMap[machine].IsActive = false;
            this.MachineInfoMap[machine].IsPaused = true;

            if (this.MachineInfoMap[machine].PendingCounter == 0)
            {
                this.ActiveMachines.Remove(machine);
            }
            
            this.Schedule(machine);
        }

        /// <summary>
        /// Notify that the machine has halted.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyMachineHalted(Machine machine)
        {
            Utilities.WriteSchedule("<ScheduleLog> Machine {0}({1}) halted.",
                machine, machine.Id);

            this.MachineInfoMap[machine].IsActive = false;
            this.MachineInfoMap[machine].IsHalted = true;
            this.ActiveMachines.Remove(machine);
            this.Schedule(machine);
        }

        /// <summary>
        /// Notify that the machine has a pending event.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyPendingEvent(Machine machine)
        {
            if (!this.MachineInfoMap[machine].IsHalted)
            {
                this.MachineInfoMap[machine].PendingCounter++;
                this.TryAddActiveMachine(machine);
            }
        }

        /// <summary>
        /// Notify that the machine handled an event event.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyHandledEvent(Machine machine)
        {
            this.MachineInfoMap[machine].PendingCounter--;
        }

        /// <summary>
        /// Notify that an assertion has failed.
        /// </summary>
        /// <param name="machine">Machine</param>
        public void NotifyAssertionFailure()
        {

        }

        /// <summary>
        /// Resets the state of the bug-finder.
        /// </summary>
        public void Reset()
        {
            this.Scheduler.Reset();
            this.ActiveMachines.Clear();
            this.MachineInfoMap.Clear();
        }

        #endregion

        #region private bug-finder methods

        /// <summary>
        /// Tries to add a new active machine.
        /// </summary>
        /// <param name="machine">Machine</param>
        private void TryAddActiveMachine(Machine machine)
        {
            if (!this.ActiveMachines.Contains(machine))
            {
                this.ActiveMachines.Add(machine);
                if (!this.MachineInfoMap.ContainsKey(machine))
                {
                    this.MachineInfoMap.Add(machine, new MachineInfo(machine.Id));
                }
            }
        }

        /// <summary>
        /// Kills the remaining machine tasks.
        /// </summary>
        /// <param name="machine">Machine</param>
        private void Close(Machine machine)
        {
            var machines = this.ActiveMachines.FindAll(val => val.Id != machine.Id);
            foreach (var m in machines)
            {
                m.Stop();
            }

            try
            {
                machine.Stop();
            }
            catch (ScheduleCancelledException)
            {
                
            }

            this.ActiveMachines.Clear();
            this.MachineInfoMap.Clear();
        }
        
        #endregion
    }
}
