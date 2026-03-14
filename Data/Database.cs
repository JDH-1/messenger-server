using Microsoft.Data.Sqlite;

namespace MessengerServer.Data
{
    public static class Database
    {
        private static readonly string DbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "messenger_server.db");

        public static string ConnStr => $"Data Source={DbPath}";

        public static void Init()
        {
            using var con = new SqliteConnection(ConnStr);
            con.Open();

            var wal = con.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            wal.ExecuteNonQuery();

            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT UNIQUE NOT NULL COLLATE NOCASE,
                    DisplayName TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    IsOnline INTEGER DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS Messages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SenderId INTEGER NOT NULL,
                    ReceiverId INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    SentAt TEXT NOT NULL,
                    IsRead INTEGER DEFAULT 0,
                    FOREIGN KEY(SenderId) REFERENCES Users(Id),
                    FOREIGN KEY(ReceiverId) REFERENCES Users(Id)
                );
                CREATE TABLE IF NOT EXISTS Friends (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NOT NULL,
                    FriendId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UNIQUE(UserId, FriendId)
                );
                CREATE TABLE IF NOT EXISTS FriendRequests (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SenderId INTEGER NOT NULL,
                    ReceiverId INTEGER NOT NULL,
                    Status TEXT DEFAULT 'pending',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_msg_conv ON Messages(SenderId, ReceiverId);
                CREATE INDEX IF NOT EXISTS idx_msg_unread ON Messages(ReceiverId, IsRead);
                CREATE INDEX IF NOT EXISTS idx_friends_u ON Friends(UserId);
                CREATE INDEX IF NOT EXISTS idx_friends_f ON Friends(FriendId);
                CREATE INDEX IF NOT EXISTS idx_freq ON FriendRequests(ReceiverId, Status);
            ";
            cmd.ExecuteNonQuery();

            // Сбрасываем online-статус при старте (токены в памяти сброшены)
            var offline = con.CreateCommand();
            offline.CommandText = "UPDATE Users SET IsOnline=0";
            offline.ExecuteNonQuery();

            Console.WriteLine($"[DB] {DbPath}");
        }
    }
}
