
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


		// Runs a SQL query and returns result as a grouped JSON envelope.

		public async Task<string> QueryToJsonAsync(string sql)
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
	}
}