
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Model.Services
{

	// Reads data from SQL Server → JSON string.
	public class PythonSQL
	{
		private readonly string _connectionString;

		// Constructor — pass in the connection string once, reuse for many queries
		public PythonSQL(string connectionString)
		{
			if (string.IsNullOrWhiteSpace(connectionString))
				throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

			_connectionString = connectionString;
		}


		// Runs the query once and pulls out ProjectCode/CustomerCode/DataIds — shared by all the QueryToJson* variants.

		private async Task<(string ProjectCode, string CustomerCode, List<string> DataIds)> ExecuteQueryAsync(string sql)
		{
			if (string.IsNullOrWhiteSpace(sql))
				throw new ArgumentException("SQL query cannot be empty.", nameof(sql));

			string projectCode = string.Empty;
			string customerCode = string.Empty;
			var dataIds = new List<string>();

			await using var connection = new SqlConnection(_connectionString);
			await connection.OpenAsync();

			await using var command = new SqlCommand(sql, connection);
			await using var reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				// Read metadata from first row only
				if (string.IsNullOrEmpty(projectCode))
				{
					projectCode = reader["ProjectCode"]?.ToString() ?? string.Empty;
					customerCode = reader["CustomerCode"]?.ToString() ?? string.Empty;
				}

				string? dataId = reader["DataIds"]?.ToString();
				if (!string.IsNullOrWhiteSpace(dataId))
					dataIds.Add(dataId);
			}

			return (projectCode, customerCode, dataIds);
		}

		private static string BuildEnvelopeJson(string projectCode, string customerCode, List<string> dataIds)
		{
			// Build envelope format — matches predict_equipment.py input
			var envelope = new
			{
				project_code = projectCode,
				customer_code = customerCode,
				data_ids = dataIds
			};

			return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
			{
				WriteIndented = true
			});
		}


		// Runs a SQL query and returns result as a grouped JSON envelope.

		public async Task<string> QueryToJsonAsync(string sql)
		{
			var (projectCode, customerCode, dataIds) = await ExecuteQueryAsync(sql);
			return BuildEnvelopeJson(projectCode, customerCode, dataIds);
		}


		// Same as QueryToJsonAsync but also saves the result to a .json file.

		public async Task<string> QueryToJsonFileAsync(string sql, string outputPath)
		{
			string json = await QueryToJsonAsync(sql);

			string? directory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory); // creates folder if missing

			await File.WriteAllTextAsync(outputPath, json);

			return json; // also return the string in case caller needs it
		}


		// Same as QueryToJsonFileAsync, but names the file after the ProjectCode found in the
		// query results instead of a fixed filename, e.g. "A9998_devices.json".
		// Returns the full path that was written to, since the filename isn't known ahead of time.

		public async Task<string> QueryToJsonFileByProjectCodeAsync(string sql, string outputDirectory, string suffix = "_devices.json")
		{
			var (projectCode, customerCode, dataIds) = await ExecuteQueryAsync(sql);

			if (string.IsNullOrWhiteSpace(projectCode))
				throw new InvalidOperationException("ProjectCode not found in query result — cannot name output file.");

			string json = BuildEnvelopeJson(projectCode, customerCode, dataIds);

			Directory.CreateDirectory(outputDirectory); // creates folder if missing

			string outputPath = Path.Combine(outputDirectory, $"{projectCode}{suffix}");
			await File.WriteAllTextAsync(outputPath, json);

			return outputPath;
		}
	}
}