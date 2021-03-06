﻿// -----------------------------------------------------------------------
//  <copyright file="TaskActions.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Database.Impl;

namespace Raven.Database.FileSystem.Actions
{
	public class TaskActions : ActionsBase
	{
		private readonly ConcurrentDictionary<long, PendingTaskWithStateAndDescription> pendingTasks = new ConcurrentDictionary<long, PendingTaskWithStateAndDescription>();

		private readonly IObservable<long> timer;

		private long pendingTaskCounter;

		public TaskActions(RavenFileSystem fileSystem, ILog log)
			: base(fileSystem, log)
		{
			timer = Observable.Interval(TimeSpan.FromMinutes(1));

			InitializeTimer();
		}

		private void InitializeTimer()
		{
			timer.Subscribe(tick => ClearCompletedPendingTasks());
		}

		private void ClearCompletedPendingTasks()
		{
			foreach (var taskAndState in pendingTasks)
			{
				var task = taskAndState.Value.Task;
				if (task.IsCompleted || task.IsCanceled || task.IsFaulted)
				{
					PendingTaskWithStateAndDescription value;
					pendingTasks.TryRemove(taskAndState.Key, out value);
				}
				if (task.Exception != null)
				{
					Log.InfoException("Failed to execute background task " + taskAndState.Key, task.Exception);
				}
			}
		}

		public List<PendingTaskDescriptionAndStatus> GetAll()
		{
			return pendingTasks.Select(x =>
			{
				var ex = (x.Value.Task.IsFaulted || x.Value.Task.IsCanceled) ? x.Value.Task.Exception.ExtractSingleInnerException() : null;
				var taskStatus = x.Value.Task.Status;
				if (taskStatus == TaskStatus.WaitingForActivation)
					taskStatus = TaskStatus.Running; //aysnc task status is always WaitingForActivation
				return new PendingTaskDescriptionAndStatus
					   {
						   Id = x.Key,
						   Payload = x.Value.Description.Payload,
						   StartTime = x.Value.Description.StartTime,
						   TaskStatus = taskStatus,
						   TaskType = x.Value.Description.TaskType,
						   Exception = ex
					   };
			}).ToList();
		}

		public void AddTask(Task task, IOperationState state, PendingTaskDescription description, out long id, CancellationTokenSource tokenSource = null)
		{
			if (task.Status == TaskStatus.Created)
				throw new ArgumentException("Task must be started before it gets added to the database.", "task");
			var localId = id = Interlocked.Increment(ref pendingTaskCounter);
			pendingTasks.TryAdd(localId, new PendingTaskWithStateAndDescription
			{
				Task = task,
				State = state,
				Description = description,
				TokenSource = tokenSource
			});
		}

		public void RemoveTask(long taskId)
		{
			PendingTaskWithStateAndDescription value;
			pendingTasks.TryRemove(taskId, out value);
		}

		public object KillTask(long id)
		{
			PendingTaskWithStateAndDescription value;
			if (pendingTasks.TryGetValue(id, out value))
			{
				if (!value.Task.IsFaulted && !value.Task.IsCanceled && !value.Task.IsCompleted)
				{
					if (value.TokenSource != null)
					{
						value.TokenSource.Cancel();
					}
				}

				return value.State;
			}
			return null;
		}

		public object GetTaskState(long id)
		{
			PendingTaskWithStateAndDescription value;
			if (pendingTasks.TryGetValue(id, out value))
			{
				return value.State;
			}
			return null;
		}

		public void Dispose(ExceptionAggregator exceptionAggregator)
		{
			foreach (var pendingTaskAndState in pendingTasks.Select(shouldDispose => shouldDispose.Value))
			{
				exceptionAggregator.Execute(() =>
				{
					try
					{
#if DEBUG
						pendingTaskAndState.Task.Wait(3000);
#else
						pendingTaskAndState.Task.Wait();
#endif
					}
					catch (Exception)
					{
						// we explictly don't care about this during shutdown
					}
				});
			}

			pendingTasks.Clear();
		}

		public class PendingTaskWithStateAndDescription
		{
			public Task Task;
			public IOperationState State;
			public PendingTaskDescription Description;
			public CancellationTokenSource TokenSource;
		}

		public class PendingTaskDescription
		{
			public string Payload;

			public PendingTaskType TaskType;

			public DateTime StartTime;
		}

		public class PendingTaskDescriptionAndStatus : PendingTaskDescription
		{
			public long Id;
			public TaskStatus TaskStatus;
			public Exception Exception;
		}

		public enum PendingTaskType
		{
			ExportFileSystem,
			ImportFileSystem,
			DeleteFilesByQuery
		}
	}
}