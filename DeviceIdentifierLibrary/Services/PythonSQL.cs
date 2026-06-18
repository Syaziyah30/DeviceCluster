//using Microsoft.Data.SqlClient;
//using System.Text.Json;

//namespace DeviceIdentifierLibrary.Services
//{
//	/// <summary>
//	/// Reads data from  : SQL Server →  JSON string.
//	/// Used to supply reference data (e.g. device lists) to Python prediction scripts.
//	/// </summary>
//	public class PythonSQL
//	{
//		private readonly string _connectionString;

//		// Constructor — pass in the connection string once, reuse for many queries
//		public PythonSQL(string connectionString)
//		{
//			if (string.IsNullOrWhiteSpace(connectionString))
//				throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

//			_connectionString = connectionString;
//		}

//		/// <summary> - WILL DELETE SOON
//		/// Runs a SQL query and returns all rows as a JSON string.
//		/// Each row becomes a JSON object { "ColumnName": value, ... }
//		/// All rows are wrapped in a JSON array [ {...}, {...} ]
//		/// </summary>
//		/// <param name="sql">The SQL query to run (SELECT ...)</param>
//		/// <returns>JSON array string of results</returns>
//		public async Task<string> QueryToJsonAsync(string sql)
//		{
//			if (string.IsNullOrWhiteSpace(sql))
//				throw new ArgumentException("SQL query cannot be empty.", nameof(sql));

//			var rows = new List<Dictionary<string, object?>>();

//			await using var connection = new SqlConnection(_connectionString);
//			await connection.OpenAsync();

//			await using var command = new SqlCommand(sql, connection);
//			await using var reader = await command.ExecuteReaderAsync();

//			while (await reader.ReadAsync())
//			{
//				var row = new Dictionary<string, object?>();

//				for (int i = 0; i < reader.FieldCount; i++)
//				{
//					string columnName = reader.GetName(i);
//					object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
//					row[columnName] = value;
//				}

//				rows.Add(row);
//			}

//			// Serialize to pretty JSON (readable) — swap to default options if you want compact
//			string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
//			{
//				WriteIndented = true
//			});

//			return json;
//		}

//		/// <summary>
//		/// Same as QueryToJsonAsync but also saves the result to a .json file.
//		/// Useful when Python scripts need to read the data from disk.
//		/// </summary>
//		/// <param name="sql">The SQL query to run</param>
//		/// <param name="outputPath">Full file path to save JSON, e.g. "C:\data\devices.json"</param>
//		public async Task<string> QueryToJsonFileAsync(string sql, string outputPath)
//		{
//			string json = await QueryToJsonAsync(sql);

//			string? directory = Path.GetDirectoryName(outputPath);
//			if (!string.IsNullOrEmpty(directory))
//				Directory.CreateDirectory(directory); // creates folder if missing

//			await File.WriteAllTextAsync(outputPath, json);

//			return json; // also return the string in case caller needs it
//		}
//	}
//}

using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace DeviceIdentifierLibrary.Services
{
	/// <summary>
	/// Reads data from SQL Server → JSON string.
	/// Used to supply reference data (e.g. device lists) to Python prediction scripts.
	/// </summary>
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

		/// <summary>
		/// Runs a SQL query and returns result as a grouped JSON envelope.
		/// Format: { "project_code": "...", "customer_code": "...", "data_ids": [...] }
		/// Matches the input format expected by predict_equipment.py.
		/// </summary>
		/// <param name="sql">The SQL query to run (SELECT ...)</param>
		/// <returns>JSON envelope string</returns>
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

		/// <summary>
		/// Same as QueryToJsonAsync but also saves the result to a .json file.
		/// Useful when Python scripts need to read the data from disk.
		/// </summary>
		/// <param name="sql">The SQL query to run</param>
		/// <param name="outputPath">Full file path to save JSON, e.g. "C:\data\devices.json"</param>
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