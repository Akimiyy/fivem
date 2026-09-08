using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CitizenFX.Core
{
	class CitizenSynchronizationContext : SynchronizationContext
	{
		private static readonly Queue<Action> m_scheduledTasks = new Queue<Action>();

		public override void Post(SendOrPostCallback d, object state)
		{
			if (d == null) return;

			lock (m_scheduledTasks)
			{
				m_scheduledTasks.Enqueue(() => d(state));
			}
		}

		[SecuritySafeCritical]
		public static void Tick()
		{
			var flowBlock = CitizenTaskScheduler.SuppressFlow();

			try
			{
				int taskCount;
				Action[] tasks;

				lock (m_scheduledTasks)
				{
					taskCount = m_scheduledTasks.Count;
					if (taskCount == 0) return;
					
					tasks = new Action[taskCount];
					for (int i = 0; i < taskCount; i++)
					{
						tasks[i] = m_scheduledTasks.Dequeue();
					}
				}

				// Processing tasks outside of the synchronization lock prevents worker threads from blocking during Post() operations
				for (int i = 0; i < taskCount; i++)
				{
					try
					{
						tasks[i]();
					}
					catch (Exception e)
					{
						InternalManager.PrintErrorInternal("task continuation", e);
					}
				}
			}
			finally
			{
				flowBlock?.Undo();
			}
		}

		public override SynchronizationContext CreateCopy()
		{
			return this;
		}
	}

	class CitizenTaskScheduler : TaskScheduler
	{
		private static readonly object m_inTickTasksLock = new object();
		
		private Dictionary<int, Task> m_inTickTasks = new Dictionary<int, Task>();
		private readonly Dictionary<int, Task> m_runningTasks = new Dictionary<int, Task>();

		protected CitizenTaskScheduler()
		{
		}

		[SecurityCritical]
		protected override void QueueTask(Task task)
		{
			if (task == null) return;
			lock (m_inTickTasksLock)
			{
				if (m_inTickTasks != null)
				{
					m_inTickTasks[task.Id] = task;
				}
			}

			lock (m_runningTasks)
			{
				m_runningTasks[task.Id] = task;
			}
		}

		[SecurityCritical]
		protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
		{
			if (!taskWasPreviouslyQueued)
			{
				return TryExecuteTask(task);
			}
			return false;
		}

		[SecurityCritical]
		protected override IEnumerable<Task> GetScheduledTasks()
		{
			lock (m_runningTasks)
			{
				return new List<Task>(m_runningTasks.Values);
			}
		}

		public override int MaximumConcurrencyLevel => 1;

		[SecuritySafeCritical]
		public void Tick()
		{
			var flowBlock = SuppressFlow();

			try
			{
				Task[] tasks;

				lock (m_runningTasks)
				{
					if (m_runningTasks.Count == 0 && (m_inTickTasks == null || m_inTickTasks.Count == 0))
						return;

					tasks = m_runningTasks.Values.ToArray();
				}

				// Ticks should be reentrant (Tick might invoke TriggerEvent, e.g.)
				Dictionary<int, Task> lastInTickTasks;

				lock (m_inTickTasksLock)
				{
					lastInTickTasks = m_inTickTasks;
					m_inTickTasks = new Dictionary<int, Task>();
				}

				do
				{
					using (var scope = new ProfilerScope(() => "task iteration"))
					{
						// Foreach on arrays compiled under older Mono variants can inject unnecessary enumerator lifecycle allocations.
						for (int i = 0; i < tasks.Length; i++)
						{
							var task = tasks[i];
							if (task == null) continue;

							InvokeTryExecuteTask(task);

							if (task.Exception != null)
							{
								foreach (var innerExc in task.Exception.Flatten().InnerExceptions)
								{
									Debug.WriteLine($"Exception thrown by a task: {innerExc}");
								}
							}

							if (task.IsCompleted || task.IsFaulted || task.IsCanceled)
							{
								lock (m_runningTasks)
								{
									m_runningTasks.Remove(task.Id);
								}
							}
						}

						lock (m_inTickTasksLock)
						{
							if (m_inTickTasks != null && m_inTickTasks.Count > 0)
							{
								tasks = m_inTickTasks.Values.ToArray();
								m_inTickTasks.Clear();
							}
							else
							{
								tasks = Array.Empty<Task>(); // Pool an empty static array instead of generating a new instance
							}
						}
					}
				} while (tasks.Length != 0);

				lock (m_inTickTasksLock)
				{
					m_inTickTasks = lastInTickTasks;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Fatal exception caught inside TaskScheduler Tick Loop: {ex}");
			}
			finally
			{
				flowBlock?.Undo();
			}
		}

		internal static AsyncFlowControl? SuppressFlow()
		{
			if (!ExecutionContext.IsFlowSuppressed())
			{
				return ExecutionContext.SuppressFlow();
			}
			return null;
		}

		[SecuritySafeCritical]
		private bool InvokeTryExecuteTask(Task task)
		{
			return TryExecuteTask(task);
		}

		private static readonly FieldInfo ms_taskFieldInfo = typeof(Task).GetField("m_action", BindingFlags.Instance | BindingFlags.NonPublic);

		private string GetTaskName(Task task)
		{
			if (task == null) return "NullTask";
			if (ms_taskFieldInfo == null) return task.ToString();

			var action = ms_taskFieldInfo.GetValue(task);

			if (action is Delegate deleg && deleg.Method != null)
			{
				var declaringType = deleg.Method.DeclaringType?.Name ?? "UnknownType";
				return $"{declaringType} -> task {deleg.Method.Name}";
			}

			return action?.ToString() ?? task.ToString();
		}

		[SecuritySafeCritical]
		public static void Create()
		{
			Instance = new CitizenTaskScheduler();
			Factory = new TaskFactory(Instance);

			TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
			TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		}

		[SecuritySafeCritical]
		public static void MakeDefault()
		{
			var field = typeof(TaskScheduler).GetField("s_defaultTaskScheduler", BindingFlags.Static | BindingFlags.NonPublic);
			if (field != null) field.SetValue(null, Instance);

			field = typeof(Task).GetField("<Factory>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);

			if (field == null)
			{
				field = typeof(Task).GetField("s_factory", BindingFlags.Static | BindingFlags.NonPublic);
			}

			if (field != null) field.SetValue(null, Factory);
		}

		private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
		{
			if (e.Exception != null)
			{
				var sb = new StringBuilder("Unhandled task exception:");
				foreach (var inner in e.Exception.Flatten().InnerExceptions)
				{
					sb.Append($"\n[Exception] {inner.Message} | StackTrace: {inner.StackTrace}");
				}
				Debug.WriteLine(sb.ToString());
			}

			e.SetObserved();
		}

		public static TaskFactory Factory { get; private set; }
		public static CitizenTaskScheduler Instance { get; private set; }
	}
}
