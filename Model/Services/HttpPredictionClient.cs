using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Model.Services
{
	// Calls the ML prediction service over HTTP instead of starting a local Python
	// process. The models stay loaded in the service's memory, so a call costs a
	// request rather than an interpreter start plus a model load.
	//
	// Nothing on the calling machine needs Python, the ML packages, or the .pkl
	// files. That is the whole point of it.
	public class HttpPredictionClient : IPredictionClient, IDisposable
	{
		private readonly HttpClient _http;
		private readonly bool _ownsHttp;

		// baseUrl is the service root, e.g. "http://128.100.8.213:8000".
		public HttpPredictionClient(string baseUrl, TimeSpan? timeout = null)
		{
			if (string.IsNullOrWhiteSpace(baseUrl))
				throw new ArgumentException("A service base URL is required.", nameof(baseUrl));

			_http = new HttpClient
			{
				BaseAddress = new Uri(baseUrl),
				Timeout = timeout ?? TimeSpan.FromMinutes(10)
			};
			_ownsHttp = true;
		}

		// For callers that already own an HttpClient and want it reused.
		public HttpPredictionClient(HttpClient http)
		{
			_http = http ?? throw new ArgumentNullException(nameof(http));
			_ownsHttp = false;
		}

		public Task<string> PredictDeviceTypeAsync(object request)
			=> PostAsync("/predict/device-type", request);

		public Task<string> PredictSectionClusterAsync(object request)
			=> PostAsync("/predict/section-cluster", request);

		public Task<string> PredictTopClustersAsync(object request)
			=> PostAsync("/predict/top-clusters", request);

		private async Task<string> PostAsync(string path, object body)
		{
			using var content = new StringContent(
				JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

			HttpResponseMessage response;
			try
			{
				response = await _http.PostAsync(path, content);
			}
			catch (HttpRequestException ex)
			{
				throw new HttpRequestException(
					$"Could not reach the ML service at {_http.BaseAddress}{path.TrimStart('/')}. " +
					"Check the service is running and the URL is correct.", ex);
			}

			using (response)
			{
				string text = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
					throw new HttpRequestException(
						$"ML service {path} returned {(int)response.StatusCode} " +
						$"{response.ReasonPhrase}: {Truncate(text, 400)}");

				return text;
			}
		}

		private static string Truncate(string s, int max)
			=> string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

		public void Dispose()
		{
			if (_ownsHttp)
				_http.Dispose();
			GC.SuppressFinalize(this);
		}
	}
}
