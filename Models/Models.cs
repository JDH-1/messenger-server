namespace MessengerServer.Models
{
    public record RegisterDto(string Username, string DisplayName, string Password);
    public record LoginDto(string Username, string Password);
    public record SendMessageDto(int SenderId, string Token, int ReceiverId, string Content);
    public record SendFriendRequestDto(int SenderId, string Token, int ReceiverId);
    public record RespondFriendDto(int UserId, string Token, int RequestId, bool Accept);
    public record SetOnlineDto(int UserId, string Token, bool Online);
    public record TypingDto(int UserId, string Token, int ToUserId);

    public record AuthResponse(bool Ok, string Message, int UserId, string Token, string DisplayName, string Username);
    public record ApiResponse(bool Ok, string Message);
}
