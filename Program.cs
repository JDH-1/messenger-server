using MessengerServer.Data;
using MessengerServer.Hubs;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

Database.Init();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

// Порт из переменной окружения (нужно для Render/Railway)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.MapHub<ChatHub>("/chathub");

// ════════════════════════════════════════════
//  HEALTH CHECK — нужен для keepalive и мониторинга
// ════════════════════════════════════════════

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// ════════════════════════════════════════════
//  AUTH
// ════════════════════════════════════════════

app.MapPost("/auth/register", (RegisterDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Username) || dto.Username.Trim().Length < 3)
        return Results.Ok(new AuthResponse(false, "Логин должен быть не менее 3 символов", 0, "", "", ""));
    if (dto.Username.Trim().Length > 32)
        return Results.Ok(new AuthResponse(false, "Логин не более 32 символов", 0, "", "", ""));
    if (string.IsNullOrWhiteSpace(dto.DisplayName) || dto.DisplayName.Trim().Length < 1)
        return Results.Ok(new AuthResponse(false, "Укажите отображаемое имя", 0, "", "", ""));
    if (dto.DisplayName.Trim().Length > 64)
        return Results.Ok(new AuthResponse(false, "Имя не более 64 символов", 0, "", "", ""));
    if (dto.Password == null || dto.Password.Length < 6)
        return Results.Ok(new AuthResponse(false, "Пароль должен быть не менее 6 символов", 0, "", "", ""));
    if (!dto.Username.Trim().All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
        return Results.Ok(new AuthResponse(false, "Логин: только буквы, цифры, _ и -", 0, "", "", ""));

    try
    {
        using var con = new SqliteConnection(Database.ConnStr);
        con.Open();
        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 11);
        var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (Username, DisplayName, PasswordHash, CreatedAt) VALUES ($u,$d,$h,$t)";
        cmd.Parameters.AddWithValue("$u", dto.Username.Trim().ToLower());
        cmd.Parameters.AddWithValue("$d", dto.DisplayName.Trim());
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        var idCmd = con.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        int userId = Convert.ToInt32(idCmd.ExecuteScalar());
        var token = TokenService.GenerateToken(userId);
        return Results.Ok(new AuthResponse(true, "Регистрация успешна!", userId, token, dto.DisplayName.Trim(), dto.Username.Trim().ToLower()));
    }
    catch
    {
        return Results.Ok(new AuthResponse(false, "Это имя пользователя уже занято", 0, "", "", ""));
    }
});

app.MapPost("/auth/login", (LoginDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password))
        return Results.Ok(new AuthResponse(false, "Заполните все поля", 0, "", "", ""));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT Id, Username, DisplayName, PasswordHash FROM Users WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", dto.Username.Trim().ToLower());
    using var r = cmd.ExecuteReader();
    if (!r.Read())
        return Results.Ok(new AuthResponse(false, "Пользователь не найден", 0, "", "", ""));

    int id = r.GetInt32(0);
    string username = r.GetString(1);
    string display = r.GetString(2);
    string hash = r.GetString(3);
    r.Close();

    if (!BCrypt.Net.BCrypt.Verify(dto.Password, hash))
        return Results.Ok(new AuthResponse(false, "Неверный пароль", 0, "", "", ""));

    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE Users SET IsOnline=1 WHERE Id=$id";
    upd.Parameters.AddWithValue("$id", id);
    upd.ExecuteNonQuery();

    var token = TokenService.GenerateToken(id);
    return Results.Ok(new AuthResponse(true, "Вход выполнен!", id, token, display, username));
});

app.MapPost("/auth/logout", (SetOnlineDto dto) =>
{
    if (!TokenService.Validate(dto.UserId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "UPDATE Users SET IsOnline=0 WHERE Id=$id";
    cmd.Parameters.AddWithValue("$id", dto.UserId);
    cmd.ExecuteNonQuery();
    TokenService.Remove(dto.UserId);
    return Results.Ok(new ApiResponse(true, "Выход выполнен"));
});

// ════════════════════════════════════════════
//  USERS
// ════════════════════════════════════════════

app.MapGet("/users/search", (string query, int currentUserId, string token) =>
{
    if (!TokenService.Validate(currentUserId, token))
        return Results.Ok(new List<object>());
    if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
        return Results.Ok(new List<object>());

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT Id, Username, DisplayName, IsOnline FROM Users
                        WHERE (Username LIKE $q OR DisplayName LIKE $q) AND Id != $id LIMIT 20";
    cmd.Parameters.AddWithValue("$q", $"%{query}%");
    cmd.Parameters.AddWithValue("$id", currentUserId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new { Id = r.GetInt32(0), Username = r.GetString(1), DisplayName = r.GetString(2), IsOnline = r.GetInt32(3) == 1 });
    return Results.Ok(list);
});

// ════════════════════════════════════════════
//  MESSAGES
// ════════════════════════════════════════════

app.MapPost("/messages/send", async (SendMessageDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.SenderId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));
    if (string.IsNullOrWhiteSpace(dto.Content))
        return Results.Ok(new ApiResponse(false, "Сообщение не может быть пустым"));
    if (dto.Content.Length > 4000)
        return Results.Ok(new ApiResponse(false, "Сообщение слишком длинное"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();

    var nameCmd = con.CreateCommand();
    nameCmd.CommandText = "SELECT DisplayName FROM Users WHERE Id=$id";
    nameCmd.Parameters.AddWithValue("$id", dto.SenderId);
    var senderName = nameCmd.ExecuteScalar()?.ToString() ?? "";

    var now = DateTime.UtcNow;
    var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO Messages (SenderId, ReceiverId, Content, SentAt) VALUES ($s,$r,$c,$t)";
    cmd.Parameters.AddWithValue("$s", dto.SenderId);
    cmd.Parameters.AddWithValue("$r", dto.ReceiverId);
    cmd.Parameters.AddWithValue("$c", dto.Content.Trim());
    cmd.Parameters.AddWithValue("$t", now.ToString("o"));
    cmd.ExecuteNonQuery();

    var idCmd = con.CreateCommand();
    idCmd.CommandText = "SELECT last_insert_rowid()";
    int msgId = Convert.ToInt32(idCmd.ExecuteScalar());

    var msgData = new { Id = msgId, SenderId = dto.SenderId, SenderName = senderName, Content = dto.Content.Trim(), SentAt = now };
    await hub.Clients.Group($"user_{dto.ReceiverId}").SendAsync("ReceiveMessage", msgData);

    return Results.Ok(new ApiResponse(true, "Отправлено"));
});

app.MapGet("/messages", (int userId, int friendId, string token) =>
{
    if (!TokenService.Validate(userId, token))
        return Results.Ok(new List<object>());

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT m.Id, m.SenderId, m.ReceiverId, m.Content, m.SentAt, m.IsRead, u.DisplayName
        FROM Messages m JOIN Users u ON u.Id = m.SenderId
        WHERE (m.SenderId=$u AND m.ReceiverId=$f) OR (m.SenderId=$f AND m.ReceiverId=$u)
        ORDER BY m.SentAt ASC LIMIT 200";
    cmd.Parameters.AddWithValue("$u", userId);
    cmd.Parameters.AddWithValue("$f", friendId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new
        {
            Id = r.GetInt32(0),
            SenderId = r.GetInt32(1),
            ReceiverId = r.GetInt32(2),
            Content = r.GetString(3),
            SentAt = DateTime.Parse(r.GetString(4)),
            IsRead = r.GetInt32(5) == 1,
            SenderName = r.GetString(6)
        });
    r.Close();

    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE Messages SET IsRead=1 WHERE SenderId=$f AND ReceiverId=$u AND IsRead=0";
    upd.Parameters.AddWithValue("$f", friendId);
    upd.Parameters.AddWithValue("$u", userId);
    upd.ExecuteNonQuery();

    return Results.Ok(list);
});

app.MapGet("/messages/unread", (int userId, int friendId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(0);
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE SenderId=$f AND ReceiverId=$u AND IsRead=0";
    cmd.Parameters.AddWithValue("$f", friendId);
    cmd.Parameters.AddWithValue("$u", userId);
    return Results.Ok(Convert.ToInt32(cmd.ExecuteScalar()));
});

app.MapDelete("/messages/{id}", (int id, int userId, string token) =>
{
    if (!TokenService.Validate(userId, token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var check = con.CreateCommand();
    check.CommandText = "SELECT SenderId FROM Messages WHERE Id=$id";
    check.Parameters.AddWithValue("$id", id);
    var senderId = check.ExecuteScalar();
    if (senderId == null) return Results.Ok(new ApiResponse(false, "Не найдено"));
    if (Convert.ToInt32(senderId) != userId) return Results.Ok(new ApiResponse(false, "Нет прав"));
    var del = con.CreateCommand();
    del.CommandText = "DELETE FROM Messages WHERE Id=$id";
    del.Parameters.AddWithValue("$id", id);
    del.ExecuteNonQuery();
    return Results.Ok(new ApiResponse(true, "Удалено"));
});

// ════════════════════════════════════════════
//  FRIENDS
// ════════════════════════════════════════════

app.MapGet("/friends", (int userId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(new List<object>());
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT u.Id, u.Username, u.DisplayName, u.IsOnline
        FROM Friends f
        JOIN Users u ON u.Id = CASE WHEN f.UserId=$id THEN f.FriendId ELSE f.UserId END
        WHERE f.UserId=$id OR f.FriendId=$id
        ORDER BY u.IsOnline DESC, u.DisplayName ASC";
    cmd.Parameters.AddWithValue("$id", userId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new { Id = r.GetInt32(0), Username = r.GetString(1), DisplayName = r.GetString(2), IsOnline = r.GetInt32(3) == 1 });
    return Results.Ok(list);
});

app.MapPost("/friends/request", async (SendFriendRequestDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.SenderId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));
    if (dto.SenderId == dto.ReceiverId)
        return Results.Ok(new ApiResponse(false, "Нельзя добавить себя в друзья"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();

    var check = con.CreateCommand();
    check.CommandText = @"SELECT COUNT(*) FROM FriendRequests
        WHERE ((SenderId=$s AND ReceiverId=$r) OR (SenderId=$r AND ReceiverId=$s)) AND Status='pending'";
    check.Parameters.AddWithValue("$s", dto.SenderId);
    check.Parameters.AddWithValue("$r", dto.ReceiverId);
    if (Convert.ToInt32(check.ExecuteScalar()) > 0)
        return Results.Ok(new ApiResponse(false, "Заявка уже отправлена"));

    var friends = con.CreateCommand();
    friends.CommandText = "SELECT COUNT(*) FROM Friends WHERE (UserId=$s AND FriendId=$r) OR (UserId=$r AND FriendId=$s)";
    friends.Parameters.AddWithValue("$s", dto.SenderId);
    friends.Parameters.AddWithValue("$r", dto.ReceiverId);
    if (Convert.ToInt32(friends.ExecuteScalar()) > 0)
        return Results.Ok(new ApiResponse(false, "Вы уже друзья"));

    var nameCmd = con.CreateCommand();
    nameCmd.CommandText = "SELECT DisplayName FROM Users WHERE Id=$id";
    nameCmd.Parameters.AddWithValue("$id", dto.SenderId);
    var senderName = nameCmd.ExecuteScalar()?.ToString() ?? "";

    var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO FriendRequests (SenderId, ReceiverId, CreatedAt) VALUES ($s,$r,$t)";
    cmd.Parameters.AddWithValue("$s", dto.SenderId);
    cmd.Parameters.AddWithValue("$r", dto.ReceiverId);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();

    await hub.Clients.Group($"user_{dto.ReceiverId}").SendAsync("FriendRequest", new { FromId = dto.SenderId, FromName = senderName });
    return Results.Ok(new ApiResponse(true, "Заявка отправлена!"));
});

app.MapGet("/friends/requests", (int userId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(new List<object>());
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT fr.Id, fr.SenderId, fr.ReceiverId, fr.Status, fr.CreatedAt, u.DisplayName
        FROM FriendRequests fr JOIN Users u ON u.Id = fr.SenderId
        WHERE fr.ReceiverId=$id AND fr.Status='pending' ORDER BY fr.CreatedAt DESC";
    cmd.Parameters.AddWithValue("$id", userId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new
        {
            Id = r.GetInt32(0), SenderId = r.GetInt32(1), ReceiverId = r.GetInt32(2),
            Status = r.GetString(3), CreatedAt = DateTime.Parse(r.GetString(4)), SenderName = r.GetString(5)
        });
    return Results.Ok(list);
});

app.MapPost("/friends/respond", async (RespondFriendDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.UserId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var get = con.CreateCommand();
    get.CommandText = "SELECT SenderId, ReceiverId FROM FriendRequests WHERE Id=$id AND ReceiverId=$uid";
    get.Parameters.AddWithValue("$id", dto.RequestId);
    get.Parameters.AddWithValue("$uid", dto.UserId);
    using var r = get.ExecuteReader();
    if (!r.Read()) return Results.Ok(new ApiResponse(false, "Заявка не найдена"));
    int senderId = r.GetInt32(0), receiverId = r.GetInt32(1);
    r.Close();

    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE FriendRequests SET Status=$s WHERE Id=$id";
    upd.Parameters.AddWithValue("$s", dto.Accept ? "accepted" : "declined");
    upd.Parameters.AddWithValue("$id", dto.RequestId);
    upd.ExecuteNonQuery();

    if (dto.Accept)
    {
        var ins = con.CreateCommand();
        ins.CommandText = "INSERT OR IGNORE INTO Friends (UserId, FriendId, CreatedAt) VALUES ($u,$f,$t)";
        ins.Parameters.AddWithValue("$u", senderId);
        ins.Parameters.AddWithValue("$f", receiverId);
        ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        ins.ExecuteNonQuery();

        var nameCmd = con.CreateCommand();
        nameCmd.CommandText = "SELECT DisplayName FROM Users WHERE Id=$id";
        nameCmd.Parameters.AddWithValue("$id", receiverId);
        var accepterName = nameCmd.ExecuteScalar()?.ToString() ?? "";
        await hub.Clients.Group($"user_{senderId}").SendAsync("FriendRequestAccepted", new { ByName = accepterName });
    }
    return Results.Ok(new ApiResponse(true, dto.Accept ? "Принято" : "Отклонено"));
});

app.MapGet("/friends/areFriends", (int userId, int friendId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(false);
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Friends WHERE (UserId=$u AND FriendId=$f) OR (UserId=$f AND FriendId=$u)";
    cmd.Parameters.AddWithValue("$u", userId);
    cmd.Parameters.AddWithValue("$f", friendId);
    return Results.Ok(Convert.ToInt32(cmd.ExecuteScalar()) > 0);
});

app.MapDelete("/friends/{friendId}", (int friendId, int userId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(new ApiResponse(false, "Нет доступа"));
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var del = con.CreateCommand();
    del.CommandText = "DELETE FROM Friends WHERE (UserId=$u AND FriendId=$f) OR (UserId=$f AND FriendId=$u)";
    del.Parameters.AddWithValue("$u", userId);
    del.Parameters.AddWithValue("$f", friendId);
    del.ExecuteNonQuery();
    return Results.Ok(new ApiResponse(true, "Удалено"));
});

// ════════════════════════════════════════════
//  TYPING
// ════════════════════════════════════════════

app.MapPost("/typing", async (TypingDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.UserId, dto.Token)) return Results.Ok();
    await hub.Clients.Group($"user_{dto.ToUserId}").SendAsync("UserTyping", new { FromId = dto.UserId });
    return Results.Ok();
});

Console.WriteLine($"=== Messenger Server запущен на порту {port} ===");
app.Run();
