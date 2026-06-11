using DeviceIdentifierLibrary.ModelResult;
using System.Text.Json;

namespace DeviceIdentifierLibrary.ManualTest.Tests
{
	public static class TestPipelineResult
	{
		public static void Run()
		{
			Console.WriteLine("===== TEST: PipelineResult =====");

			// Test 1: Create object and check properties exist
			var result = new PipelineResult
			{
				DEVICE_ID = "DEV-001",
				CUSTOMER = "ACME",
				PROJECT = "P001",
				PREDICTED_SECTION = "SECTION_A",
				SECTION_CONFIDENCE = 0.91,
				PREDICTED_CLUSTER = "CLUSTER_1",
				CLUSTER_CONFIDENCE = 0.87,
				REJECTION_REASON = null,
				FORMAT_WARNING = null
			};

			Console.WriteLine($"DEVICE_ID           : {result.DEVICE_ID}");
			Console.WriteLine($"CUSTOMER            : {result.CUSTOMER}");
			Console.WriteLine($"PROJECT             : {result.PROJECT}");
			Console.WriteLine($"PREDICTED_SECTION   : {result.PREDICTED_SECTION}");
			Console.WriteLine($"SECTION_CONFIDENCE  : {result.SECTION_CONFIDENCE}");
			Console.WriteLine($"PREDICTED_CLUSTER   : {result.PREDICTED_CLUSTER}");
			Console.WriteLine($"CLUSTER_CONFIDENCE  : {result.CLUSTER_CONFIDENCE}");
			Console.WriteLine($"REJECTION_REASON    : {result.REJECTION_REASON ?? "none"}");
			Console.WriteLine($"FORMAT_WARNING      : {result.FORMAT_WARNING ?? "none"}");

			// Test 2: Deserialize from JSON string
			string fakeJson = """
            {
                "DEVICE_ID": "DEV-999",
                "CUSTOMER": "BETA",
                "PROJECT": "P002",
                "PREDICTED_SECTION": "SECTION_B",
                "SECTION_CONFIDENCE": 0.75,
                "PREDICTED_CLUSTER": "CLUSTER_2",
                "CLUSTER_CONFIDENCE": 0.60,
                "REJECTION_REASON": "Low confidence",
                "FORMAT_WARNING": null
            }
            """;

			var deserialized = JsonSerializer.Deserialize<PipelineResult>(
				fakeJson,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
			);

			Console.WriteLine();
			Console.WriteLine("-- Deserialized from JSON --");
			Console.WriteLine($"DEVICE_ID         : {deserialized.DEVICE_ID}");
			Console.WriteLine($"PREDICTED_SECTION : {deserialized.PREDICTED_SECTION}");
			Console.WriteLine($"PREDICTED_CLUSTER : {deserialized.PREDICTED_CLUSTER}");
			Console.WriteLine($"REJECTION_REASON  : {deserialized.REJECTION_REASON}");

			Console.WriteLine();
			Console.WriteLine("PipelineResult test PASSED");
			Console.WriteLine();
		}
	}
}