using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Model.Services
{
	// Runs the prediction scripts as local Python processes. The machine running
	// this needs Python, the ML packages and the .pkl model files present.
	// For the server-side alternative see HttpPredictionClient.
	public class PythonClient : IPredictionClient
	{
		private readonly string _pythonExe;
		private readonly string? _scriptDeviceType;
		private readonly string? _scriptSectionCluster;

		// Script paths are optional so existing callers that only use RunAsync
		// keep working. The IPredictionClient methods need them.
		public PythonClient(string pythonExe)
			: this(pythonExe, null, null) { }

		public PythonClient(string pythonExe, string? scriptDeviceType, string? scriptSectionCluster)
		{
			_pythonExe = pythonExe;
			_scriptDeviceType = scriptDeviceType;
			_scriptSectionCluster = scriptSectionCluster;
		}

		public Task<string> PredictDeviceTypeAsync(object request)
			=> RunAsync(Required(_scriptDeviceType, "device type"), request);

		public Task<string> PredictSectionClusterAsync(object request)
			=> RunAsync(Required(_scriptSectionCluster, "section/cluster"), request);

		// Same script as section/cluster — the payload's top_clusters action is
		// what selects the behaviour.
		public Task<string> PredictTopClustersAsync(object request)
			=> RunAsync(Required(_scriptSectionCluster, "section/cluster"), request);

		private static string Required(string? scriptPath, string which)
			=> string.IsNullOrWhiteSpace(scriptPath)
				? throw new InvalidOperationException(
					$"No {which} script path was supplied. Construct PythonClient with the " +
					"script paths to use it as an IPredictionClient, or use HttpPredictionClient.")
				: scriptPath;

		public async Task<string> RunAsync(string scriptPath, object request)
		{
			string jsonInput = JsonSerializer.Serialize(request);

			var psi = new ProcessStartInfo
			{
				FileName = _pythonExe,
				Arguments = $"-u \"{scriptPath}\"",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var process = new Process { StartInfo = psi };
			process.Start();

			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();

			try
			{
				await process.StandardInput.WriteAsync(jsonInput);
				process.StandardInput.Close();
			}
			catch (IOException) { }

			string output = await outputTask;
			string error = await errorTask;
			await process.WaitForExitAsync();

			if (process.ExitCode != 0)
				throw new Exception($"Python script error:\n{error}");

			return output.Trim();
		}
	}
}