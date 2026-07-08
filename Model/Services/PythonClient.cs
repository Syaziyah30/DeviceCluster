using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Model.Services
{
	public class PythonClient
	{
		private readonly string _pythonExe;

		public PythonClient(string pythonExe)
		{
			_pythonExe = pythonExe;
		}

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