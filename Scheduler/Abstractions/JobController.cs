﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MaeveFramework.Scheduler.Abstractions
{
    public class JobController
    {
        public JobController(JobBase job)
        {
            Job = job;
        }

        public readonly JobBase Job;
        public Task JobTask { get; private set; }
        public CancellationTokenSource JobCancelToken { get; private set; }

        public void StartJob()
        {
            if ((Job.State == JobStateEnum.NotStarted || Job.State == JobStateEnum.Stopped || Job.State == JobStateEnum.Crash) && (JobTask?.Status ?? TaskStatus.WaitingToRun) != TaskStatus.Running)
            {
                if (JobTask == null || JobTask.IsCompleted || JobTask.IsFaulted)
                {
                    JobCancelToken = new CancellationTokenSource();
                    JobTask = new Task(CreateAction(Job), JobCancelToken.Token, TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (JobCancelToken.Token.CanBeCanceled)
                JobCancelToken.Cancel(false);

            Job.State = JobStateEnum.Stopped;
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
                    try
                    {
                        Job.State = JobStateEnum.Starting;
                        Job.OnStart();
                        Job.Logger.Debug($"Job {Job.Name} started, run scheduled at {Job.NextRun}");
                    }
                    catch (Exception ex)
                    {
                        Job.Logger.Error(ex, "Exception on starting Job");
                    }
                    finally
                    {
                        Job.State = JobStateEnum.Started;
                    }

                    if (SchedulerManager.IsDisposed)
                        Job.Logger.Warn("Unable to start Job, SchedulerManager is disposed!");

                    while (!SchedulerManager.IsDisposed)
                    {
                        if (Job.State == JobStateEnum.Stopping || Job.State == JobStateEnum.Stopped || JobCancelToken.Token.IsCancellationRequested)
                            break;

                        if (Job.Schedule.CanRun())
                        {
                            try
                            {
                                Job.State = JobStateEnum.Working;
                                // Job
                                Job.Job();

                                Job.Logger.Debug($"Job {Job.Name} is now idle, next run at {Job.NextRun}");
                            }
                            catch (Exception ex)
                            {
                                Job.Logger.Error(ex, $"Exception on execution of job: {Job.Name}");
                            }
                            finally
                            {
                                Job.State = JobStateEnum.Idle;
                            }
                        }

                        if (Job.State == JobStateEnum.Stopping || Job.State == JobStateEnum.Stopped || JobCancelToken.Token.IsCancellationRequested)
                            break;

                        JobCancelToken.Token.WaitHandle.WaitOne(Job.NextRun.Subtract(DateTime.Now));
                        JobCancelToken.Token.WaitHandle.WaitOne(10);
                    }

                    // OnStop
                    throw new OperationCanceledException("End of job");
                }
                catch (OperationCanceledException ex)
                {
                    Job.Logger.Warn(ex, $"Stopping job {Job.Name}, reason: {ex.Message}");
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
                        Job.State = JobStateEnum.Stopping;
                        if (Job.State != JobStateEnum.Crash)
                            Job.OnStop();
                    }
                    catch (Exception ex)
                    {
                        Job.Logger.Error(ex, "Exception on stopping job");
                    }

                    StopJob(true);

                    Job.Logger.Debug($"Job {Job.Name} is now {Job.State}");
                    Job.State = JobStateEnum.Stopped;
                }
            });

            return action;
        }
    }
}
