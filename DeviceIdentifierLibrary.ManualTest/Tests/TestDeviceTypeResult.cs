using DeviceIdentifierLibrary.ModelResult;
using System.Text.Json;

namespace DeviceIdentifierLibrary.ManualTest.Tests
{
	public static class TestDeviceTypeResult
	{
		public static void Run()
		{
			Console.WriteLine("===== TEST: DeviceTypeResult =====");

			// Test 1: Create object and check properties exist
			var result = new DeviceTypeResult
			{
				customer = "ACME",
				data_id = "DEV-001",
				manual_check = "N",
				data_type = "Pump",
				confidence = 0.95,
				reason = "High similarity match"
			};

			Console.WriteLine($"customer      : {result.customer}");
			Console.WriteLine($"data_id       : {result.data_id}");
			Console.WriteLine($"manual_check  : {result.manual_check}");
			Console.WriteLine($"data_type     : {result.data_type}");
			Console.WriteLine($"confidence    : {result.confidence}");
			Console.WriteLine($"reason        : {result.reason}");

			// Test 2: Deserialize from JSON string (simulates what PythonClient returns)
			string fakeJson = """
            {
                "customer": "BETA",
                "data_id": "DEV-002",
                "manual_check": "Y",
                "data_type": "Valve",
                "confidence": 0.82,
                "reason": "Manual override"
            }
            """;

			var deserialized = JsonSerializer.Deserialize<DeviceTypeResult>(
				fakeJson,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
			);

			Console.WriteLine();
			Console.WriteLine("-- Deserialized from JSON --");
			Console.WriteLine($"customer  : {deserialized.customer}");
			Console.WriteLine($"data_id   : {deserialized.data_id}");
			Console.WriteLine($"data_type : {deserialized.data_type}");
			Console.WriteLine($"confidence: {deserialized.confidence}");

			Console.WriteLine();
			Console.WriteLine("DeviceTypeResult test PASSED");
			Console.WriteLine();
		}
	}
}