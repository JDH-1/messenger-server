using System.Collections.Concurrent;

namespace MessengerServer.Services
{
    public static class TokenService
    {
        private static readonly ConcurrentDictionary<int, string> _tokens = new();

        public static string GenerateToken(int userId)
        {
            var bytes = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            var token = Convert.ToBase64String(bytes);
            _tokens[userId] = token;
            return token;
        }

        public static bool Validate(int userId, string? token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            return _tokens.TryGetValue(userId, out var t) && t == token;
        }

        public static void Remove(int userId) => _tokens.TryRemove(userId, out _);
    }
}
