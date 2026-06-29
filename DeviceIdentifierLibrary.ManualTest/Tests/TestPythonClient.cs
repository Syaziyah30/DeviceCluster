using DeviceIdentifierLibrary.Services;
using DeviceIdentifierLibrary.ModelRequest;
using DeviceIdentifierLibrary.ModelResult;
using System.Text.Json;

namespace DeviceIdentifierLibrary.ManualTest.Tests
{
	public static class TestPythonClient
	{
		// ↓ Change these paths to match your machine
		private static readonly string PythonExe =
			@"C:\Users\sitisyaziyah\AppData\Local\Programs\Python\Python313\python.exe";

		private static readonly string EquipmentScript =
			//@"C:\Users\sitisyaziyah\source\repos\DeviceIdentifier2\Prediction_service\DeviceCluster\predict_equipment.py";
			@"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\DeviceCluster\predict_equipment.py";

		private static readonly string SampleJsonPath =
			//@"C:\Users\sitisyaziyah\source\repos\DeviceIdentifier2\Prediction_service\0.Equipment_Prediction\project_application\A1825.json";
			@"C:\Users\sitisyaziyah\source\repos\DeviceCluster\Prediction_service\TestDevice\A1825.json";

		public static async Task RunAsync()
		{
			Console.WriteLine("===== TEST: PythonClient =====");


			// -> dll library from [DeviceIdentifierLibrary.Services = PythonClient]
			var client = new PythonClient(PythonExe);

			// Load request from your existing JSON file
			string jsonText = File.ReadAllText(SampleJsonPath, System.Text.Encoding.UTF8);

			// -> dll library from [DeviceIdentifierLibrary.ModelRequest = DevicePredictRequest]
			var request = JsonSerializer.Deserialize<DevicePredictRequest>(jsonText);

			// Clean BOM characters (same as your original Program.cs)
			request.data_ids = request.data_ids
				.Select(id => id.Replace("\uFEFF", "").Trim())
				.Where(id => !string.IsNullOrEmpty(id))
				.ToList();

			Console.WriteLine($"Loaded {request.data_ids.Count} device IDs");
			Console.WriteLine("Calling Python script...");

			string rawJson = await client.RunAsync(EquipmentScript, request);

			Console.WriteLine("Python returned raw JSON (first 300 chars):");
			Console.WriteLine(rawJson.Substring(0, Math.Min(300, rawJson.Length)));
			Console.WriteLine();

			// Deserialize result
			// -> dll library from[DeviceIdentifierLibrary.ModelResult = DeviceTypeResult]
			var results = JsonSerializer.Deserialize<List<DeviceTypeResult>>(
				rawJson,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
			);

			Console.WriteLine($"Total results: {results.Count}");
			Console.WriteLine();
			Console.WriteLine($"{"Customer",-10} | {"Data ID",-25} | {"Data Type",-25} | {"Confidence",10} | Reason");
			Console.WriteLine(new string('-', 90));

			foreach (var r in results)
			{
				Console.WriteLine(
					$"{r.customer,-10} | " +
					$"{r.data_id,-25} | " +
					$"{r.data_type,-25} | " +
					$"{r.confidence,10:F3} | " +
					$"{r.reason}"
				);
			}

			Console.WriteLine();
			Console.WriteLine("PythonClient test PASSED");
			Console.WriteLine();
		}
	}
}