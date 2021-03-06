﻿using MaeveFramework.Helpers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MaeveFramework.Scheduler.Abstractions
{
    public class JobController
    {
        private readonly object _jobActionRygiel = new object();

        public JobController(JobBase job)
        {
            Job = job;
        }

        public readonly JobBase Job;
        private Task JobTask { get; set; }
        private CancellationTokenSource JobCancelToken { get; set; }
        private CancellationTokenSource WaitCancelToken { get; set; }

        public void StartJob()
        {
            if ((Job.State == JobStateEnum.NotStarted || Job.State == JobStateEnum.Stopped || Job.State == JobStateEnum.Crash) && (JobTask?.Status ?? TaskStatus.WaitingToRun) != TaskStatus.Running)
            {
                WaitCancelToken = new CancellationTokenSource();

                if (JobTask == null || JobTask.IsCompleted || JobTask.IsFaulted)
                {
                    JobCancelToken = new CancellationTokenSource();
                    JobTask = new Task(CreateAction(Job), JobCancelToken.Token, TaskCreationOptions.LongRunning);
                }

                JobTask.Start();
            }
            else
            {
                Job.Logger.Warn($"Cannot start job with state: {Job.State}");
            }
        }

        public void StopJob(bool force = false)
        {
            Job.State = JobStateEnum.Stopping;

            if (WaitCancelToken?.Token.CanBeCanceled ?? false)
                WaitCancelToken.Cancel(false);
            if (JobCancelToken?.Token.CanBeCanceled ?? false)
                JobCancelToken.Cancel(false);

            Job.State = JobStateEnum.Stopped;
        }

        /// <summary>
        /// Wake job and ignore schedule
        /// </summary>
        public void Wake()
        {
            if (Job.State == JobStateEnum.Stopping || Job.State == JobStateEnum.Stopped)
                throw new Exception("Cannot wake stopped job!");
            else if (Job.State == JobStateEnum.Starting || Job.State == JobStateEnum.NotStarted || Job.State == JobStateEnum.NotSet)
                throw new Exception("Cannot wake not started job!");
            else
            {
                Job.State = JobStateEnum.Wake;
                WaitCancelToken?.Cancel(false);
            }
        }

        private Action CreateAction(JobBase job)
        {
            Action action = new Action(() =>
            {
                Thread.CurrentThread.Name = $"MaeveFramework.Scheduler_{Job.Name}";

                try
                {
                    Job.State = JobStateEnum.NotStarted;

                    // OnStart
                    // Restart job if exception will be throwen in OnStart
                    Job.State = JobStateEnum.Starting;
                    Job.OnStart();
                    Job.Logger.Debug($"Job {Job.Name} started, run scheduled at {Job.NextRun}");

                    if (JobCancelToken.Token.IsCancellationRequested)
                        Job.Logger.Debug("Unable to start Job, JobCancelToken requested!");

                    Job.State = JobStateEnum.Started;

                    if (JobTask.IsCompleted)
                        Job.Logger.Debug("Job task is completed");

                    while (!JobCancelToken.IsCancellationRequested)
                    {
                        if (Job.State == JobStateEnum.Stopping || Job.State == JobStateEnum.Stopped)
                        {
                            break;
                        }

                        lock (_jobActionRygiel)
                        {
                            if (Job.Schedule.CanRun() || Job.State == JobStateEnum.Wake)
                            {
                                try
                                {
                                    if (Job.State == JobStateEnum.Wake)
                                        Job.Logger.Debug("Job is waking up");

                                    // Job
                                    Job.State = JobStateEnum.Working;
                                    job.LastRun = DateTime.Now;
                                    Job.Job();
                                }
                                catch (Exception ex)
                                {
                                    Job.Logger.Error(ex, $"Exception on execution of job: {Job.Name}");
                                }
                                finally
                                {
                                    Job.NextRun = Job.Schedule.GetNextRun();
                                    Job.Logger.Debug($"Job {Job.Name} complete, next run: {Job.NextRun}");
                                }
                            }

                            if (Job.State == JobStateEnum.Stopping || Job.State == JobStateEnum.Stopped || JobCancelToken.Token.IsCancellationRequested)
                                break;

                            try
                            {
                                Job.State = JobStateEnum.Idle;

                                if (!JobCancelToken.IsCancellationRequested)
                                {
                                    if (WaitCancelToken?.IsCancellationRequested ?? true)
                                        WaitCancelToken = new CancellationTokenSource();

                                    double waitMs = Job.NextRun.Subtract(DateTime.Now).TotalMilliseconds;
                                    Int32 waitMsInt32 = (waitMs > Int32.MaxValue)
                                    ? Int32.MaxValue
                                    : Convert.ToInt32(waitMs);

                                    JobTask.Wait(waitMsInt32, WaitCancelToken.Token);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!JobCancelToken.IsCancellationRequested && !WaitCancelToken.IsCancellationRequested)
                                {
                                    Job.Logger.Error(ex, "Exception on waiting handle, waiting 3 seconds.");
                                    JobCancelToken.Token.WaitHandle.WaitOne(3.Seconds());
                                }
                            }
                        }
                    }

                    // OnStop
                    throw new OperationCanceledException("End of job");
                }
                catch (OperationCanceledException ex)
                {
                    Job.Logger.Debug(ex, $"Stopping job {Job.Name}, reason: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Job.Logger.Error(ex, "Unhandled job exception");
                    Job.State = JobStateEnum.Crash;
                }
                finally
                {
                    try
                    {
                        if (Job.State != JobStateEnum.Crash)
                        {
                            Job.State = JobStateEnum.Stopping;
                            Job.OnStop();
                        }
                    }
                    catch (Exception ex)
                    {
                        Job.Logger.Error(ex, "Exception on stopping job");
                    }

                    Job.Logger.Debug($"Job {Job.Name} is now {Job.State}");
                    if (Job.State == JobStateEnum.Crash)
                    {
                        JobCancelToken = new CancellationTokenSource();

                        Task.Run(() =>
                        {
                            try
                            {
                                Job.Logger.Warn("Restarting job after crash in 3 seconds.");

                                JobCancelToken.Token.WaitHandle.WaitOne(3.Seconds());
                                if (!JobCancelToken.IsCancellationRequested)
                                    StartJob();
                            }
                            catch (Exception ex)
                            {
                                Job.Logger.Error(ex, "Failed to restart Job");
                            }
                        });
                    }
                    else
                    {
                        Job.State = JobStateEnum.Stopped;
                    }
                }
            });

            return action;
        }
    }
}
