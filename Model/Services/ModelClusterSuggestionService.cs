using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Model.ModelResult;

namespace Model.Services
{
	public class ModelClusterSuggestionService
	{
		private readonly PythonClient _client;
		private readonly string _scriptPath;
		private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

		public ModelClusterSuggestionService(PythonClient client, string scriptPath)
		{
			_client = client;
			_scriptPath = scriptPath;
		}

		public async Task<List<ClusterSuggestionResult>> GetTopClustersAsync(
			string deviceId, string customerCode, string projectCode, int topN = 3)
		{
			var payload = new
			{
				action = "top_clusters",
				device_id = deviceId,
				customer_code = customerCode,
				project_code = projectCode,
				top_n = topN
			};

			string json = await _client.RunAsync(_scriptPath, payload);
			var raw = JsonSerializer.Deserialize<List<TopClusterRaw>>(json, _jsonOpts);

			return raw?.Select(r => new ClusterSuggestionResult
			{
				Section = r.section,
				Cluster = r.cluster,
				ClosestDeviceId = deviceId,
				Confidence = r.probability
			}).ToList() ?? new List<ClusterSuggestionResult>();
		}

		private class TopClusterRaw
		{
			[JsonPropertyName("section")]
			public string section { get; set; } = "";

			[JsonPropertyName("cluster")]
			public string cluster { get; set; } = "";

			[JsonPropertyName("probability")]
			public double probability { get; set; }
		}
	}
}