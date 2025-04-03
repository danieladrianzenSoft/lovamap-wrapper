using System.Collections.Concurrent;

namespace WrapperApi.Services
{
	public class HeartbeatCache
	{
		private readonly ConcurrentDictionary<string, (string Message, DateTime PostedAt)> _heartbeats = new();
    	private readonly ConcurrentDictionary<string, bool> _activeJobs = new();

		public bool HasActiveJobs() => _activeJobs.Count > 0;
		
		public int ActiveJobCount => _activeJobs.Count;

		public void Update(string jobId, string message)
		{
			_heartbeats[jobId] = (message, DateTime.UtcNow);
		}

		public void MarkJobStarted(string jobId)
		{
			_activeJobs[jobId] = true;
		}

		public void MarkJobCompleted(string jobId)
		{
			_activeJobs.TryRemove(jobId, out _);
		}

		public (string Message, DateTime PostedAt)? Get(string jobId)
		{
			return _heartbeats.TryGetValue(jobId, out var value) ? value : null;
		}

		public Dictionary<string, (string Message, DateTime PostedAt)> GetAll()
		{
			return _heartbeats.ToDictionary(x => x.Key, x => x.Value);
		}

		public void Remove(string jobId)
		{
			_heartbeats.TryRemove(jobId, out _);
			_activeJobs.TryRemove(jobId, out _);
		}
	}
}


