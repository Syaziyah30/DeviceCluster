using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Logic
{
	// Loads cluster quota patterns from dbo.PatternCluster, filtered by CustomerCode.
	// The caller decides the connection string, table name, and customer; this class has
	// no opinion about any of them — same "library owns how, caller owns what" split used
	// throughout the rest of the pipeline.
	public static class QuotaCatalog
	{
		public static async Task<List<ClusterQuota>> LoadQuotasFromDbAsync(string connectionString, string tableName, string customerCode)
		{
			if (string.IsNullOrWhiteSpace(connectionString))
				throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));
			if (string.IsNullOrWhiteSpace(tableName))
				throw new ArgumentException("Table name cannot be empty.", nameof(tableName));
			if (string.IsNullOrWhiteSpace(customerCode))
				throw new ArgumentException("Customer code cannot be empty.", nameof(customerCode));

			var quotas = new List<ClusterQuota>();
			string sql = $"SELECT Section, Cluster, DeviceType, TargetCount FROM {tableName} WHERE CustomerCode = @CustomerCode";

			await using var connection = new SqlConnection(connectionString);
			await connection.OpenAsync();

			await using var command = new SqlCommand(sql, connection);
			command.Parameters.AddWithValue("@CustomerCode", customerCode);

			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				quotas.Add(new ClusterQuota
				{
					Section = reader["Section"]?.ToString() ?? string.Empty,
					Cluster = reader["Cluster"]?.ToString() ?? string.Empty,
					DeviceType = reader["DeviceType"]?.ToString() ?? string.Empty,
					TargetCount = reader["TargetCount"] != DBNull.Value ? Convert.ToInt32(reader["TargetCount"]) : 0
				});
			}

			if (quotas.Count == 0)
				throw new InvalidOperationException(
					$"No quota pattern found in '{tableName}' for customer '{customerCode}'.");

			return quotas;
		}
	}
}
