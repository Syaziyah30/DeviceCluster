using DeviceIdentifierLibrary.ManualTest.Tests;

// =======================================
// CHOOSE WHICH TEST TO RUN
// Comment out the ones you don't need
// =======================================

// Test 1: DeviceTypeResult model only (no Python needed)
TestDeviceTypeResult.Run();

// Test 2: PipelineResult model only (no Python needed)
TestPipelineResult.Run();


// Test 3: Full Python call (needs Python + script path)
await TestPythonClient.RunAsync();

Console.WriteLine("All selected tests completed.");