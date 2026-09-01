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
		private readonly IPredictionClient _client;
		private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

		// Takes IPredictionClient, so suggestions work against either local Python
		// or the ML service. The client knows where its own predictions come from,
		// so there is no script path to pass in any more.
		public ModelClusterSuggestionService(IPredictionClient client)
		{
			_client = client;
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

			string json = await _client.PredictTopClustersAsync(payload);
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