using System.Text.RegularExpressions;

namespace Logic.SimilarityScore
{
	public static class StringSimilarity
	{
		// ── Strip prefix, keep numeric + suffix only ───────────────────────
		// e.g. PU231 → "231", TE231A → "231A", CT141AF1 → "141AF1"
		public static string StripPrefix(string deviceId)
		{
			var match = Regex.Match(deviceId, @"\d.*");
			return match.Success ? match.Value : deviceId;
		}

		// ── Levenshtein Distance ───────────────────────────────────────────
		public static int LevenshteinDistance(string a, string b)
		{
			int[,] matrix = new int[a.Length + 1, b.Length + 1];

			for (int i = 0; i <= a.Length; i++) matrix[i, 0] = i;
			for (int j = 0; j <= b.Length; j++) matrix[0, j] = j;

			for (int i = 1; i <= a.Length; i++)
			{
				for (int j = 1; j <= b.Length; j++)
				{
					int cost = a[i - 1] == b[j - 1] ? 0 : 1;
					matrix[i, j] = Math.Min(
						Math.Min(matrix[i - 1, j] + 1,
								 matrix[i, j - 1] + 1),
								 matrix[i - 1, j - 1] + cost
					);
				}
			}

			return matrix[a.Length, b.Length];
		}

		// ── Levenshtein Similarity % (prefix stripped) ────────────────────
		public static double LevenshteinSimilarity(string a, string b)
		{
			string strippedA = StripPrefix(a);
			string strippedB = StripPrefix(b);

			int distance = LevenshteinDistance(strippedA, strippedB);
			int maxLength = Math.Max(strippedA.Length, strippedB.Length);
			if (maxLength == 0) return 100.0;
			return (1.0 - (double)distance / maxLength) * 100.0;
		}
	}
}