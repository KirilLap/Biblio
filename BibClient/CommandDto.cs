namespace BibClient
{
    public class CommandDto
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public string CommandId { get; set; } = "";

        // Дополнительные поля для сессии
        public string SessionType { get; set; } = "";
        public int LimitSeconds { get; set; } = 0;
        public int PaidAmount { get; set; } = 0;
    }
}