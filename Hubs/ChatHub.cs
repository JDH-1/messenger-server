using Microsoft.AspNetCore.SignalR;
using MessengerServer.Services;

namespace MessengerServer.Hubs
{
    public class ChatHub : Hub
    {
        public static readonly Dictionary<int, string> UserConnections = new();

        public async Task Register(int userId, string token)
        {
            if (!TokenService.Validate(userId, token)) return;
            lock (UserConnections)
                UserConnections[userId] = Context.ConnectionId;
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }

        public async Task SendTyping(int toUserId, string token, int fromUserId)
        {
            if (!TokenService.Validate(fromUserId, token)) return;
            await Clients.Group($"user_{toUserId}").SendAsync("UserTyping", new { FromId = fromUserId });
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            lock (UserConnections)
            {
                var entry = UserConnections.FirstOrDefault(x => x.Value == Context.ConnectionId);
                if (entry.Key != 0) UserConnections.Remove(entry.Key);
            }
            return base.OnDisconnectedAsync(exception);
        }
    }
}
