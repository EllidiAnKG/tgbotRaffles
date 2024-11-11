
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types; 
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Data.SQLite;

class Program
{
    private static ITelegramBotClient? client;
    private static ReceiverOptions? receiverOptions;
    private static string token = "7006475150:AAF2_c7cH8qVUTguc-dsMv8OVJUqxNLsL4M"; 
    private static string dbFilePath = "raffles.db"; 
    private static List<Raffle> raffles = new List<Raffle>();
    private static List<Raffle> raffleHistory = new List<Raffle>();
    private static Timer raffleTimer;
    private static TimeSpan checkInterval = TimeSpan.FromMinutes(0.1); 

    public static void Main(string[] args)
    {
        InitializeDatabase();
        LoadRaffles();

        client = new TelegramBotClient(token);
        receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        raffleTimer = new Timer(CheckRaffles, null, TimeSpan.Zero, checkInterval);

        using var cts = new CancellationTokenSource();
        client.StartReceiving(UpdateHandler, ErrorHandler, receiverOptions, cts.Token);

        Console.WriteLine("Бот работает в автономном режиме!");
        Console.ReadLine();
        Console.WriteLine("Бот остановлен полностью");
    }

    private static void InitializeDatabase()
    {
        if (System.IO.File.Exists(dbFilePath))
        {
            return;
        }

        SQLiteConnection.CreateFile(dbFilePath);

        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand(@"
            CREATE TABLE Raffle (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT UNIQUE NOT NULL,
                ScheduledTime TEXT,
                RaffleTime TEXT,
                Winner TEXT,
                Participants TEXT,
                ImageURL TEXT
            );

            CREATE TABLE RaffleHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT UNIQUE NOT NULL,
                ScheduledTime TEXT,
                RaffleTime TEXT,
                Winner TEXT,
                Participants TEXT,
                ImageURL TEXT
            );
        ", connection);
        command.ExecuteNonQuery();
    }

    private static void LoadRaffles()
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand("SELECT * FROM Raffle", connection);
        using var reader = command.ExecuteReader();

        raffles.Clear(); // Очистить список перед загрузкой
        while (reader.Read())
        {
            raffles.Add(new Raffle
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                ScheduledTime = !reader.IsDBNull(2) ? TimeSpan.Parse(reader.GetString(2)) : null,
                RaffleTime = !reader.IsDBNull(3) ? DateTime.Parse(reader.GetString(3)) : null,
                Winner = !reader.IsDBNull(4) ? reader.GetString(4) : null,
                Participants = !reader.IsDBNull(5) ? reader.GetString(5).Split(',').ToList() : new List<string>(),
                ImageURL = !reader.IsDBNull(6) ? reader.GetString(6) : null
            });
        }


    }

    private static void CheckRaffles(object? state)
    {
        foreach (var raffle in raffles.ToList())
        {
            if (raffle.ScheduledTime <= DateTime.Now.TimeOfDay)
            {
                _ = SelectWinner(client, raffle);
                raffles.Remove(raffle);
                SaveRaffle(raffle, "RaffleHistory");
                SaveRaffles();
            }
        }
    }

    private static async Task UpdateHandler(ITelegramBotClient client, Update update, CancellationToken token)
    {
        if (update.Type == UpdateType.Message && update.Message?.Text != null)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            List<long> adminIds = new List<long> { 932635238 };

            if (adminIds.Contains(chatId))
            {
                await HandleAdminCommands(client, message);
            }
            else if (message.Text == "/admin")
            {
                await client.SendTextMessageAsync(chatId, "** вы не администратор **");
            }

            if (message.Text == "/start")
            {
                await client.SendTextMessageAsync(message.Chat.Id, $"Привет {message.From?.Username}!");

                var startKeyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("История", "show_history")
                });
                await client.SendTextMessageAsync(chatId, "История розыгрышей", replyMarkup: startKeyboard);
                await ShowRaffles(client, chatId);
            }
        }

        if (update.Type == UpdateType.CallbackQuery)
        {
            await HandleCallbackQuery(client, update.CallbackQuery);
        }
    }

    private static async Task HandleAdminCommands(ITelegramBotClient client, Message message)
    {
        var chatId = message.Chat.Id;

        if (message.Text == "/admin")
        {
            await client.SendTextMessageAsync(chatId, "Вы в панели администратора.\nВыберите действие:", replyMarkup: AdminPanel());
        }

        if (message.Text.StartsWith("/create"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 2)
            {
                await client.SendTextMessageAsync(chatId, "Пожалуйста, укажите название розыгрыша после команды /create");
                return;
            }

            string giveawayName = string.Join(' ', parts.Skip(1));
            var raffle = new Raffle { Name = giveawayName };
            raffles.Add(raffle);
            SaveRaffle(raffle);

            await client.SendTextMessageAsync(chatId, $"Розыгрыш '{giveawayName}' создан!");
        }

        if (message.Text == "/history")
        {
            await ShowRaffleHistory(client, chatId);
        }

        if (message.Text.StartsWith("/delete"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 2)
            {
                await client.SendTextMessageAsync(chatId, "Пожалуйста, укажите название розыгрыша после команды /delete");
                return;
            }

            string giveawayName = string.Join(' ', parts.Skip(1));
            DeleteRaffle(giveawayName);
            var raffle = raffles.FirstOrDefault(r => r.Name.Equals(giveawayName, StringComparison.OrdinalIgnoreCase));
            if (raffle != null)
            {
                raffles.Remove(raffle);
                SaveRaffles();
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{giveawayName}' удалён.");
            }
            else
            {
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{giveawayName}' не найден.");
            }
        }
        if (message.Text.StartsWith("/setimage"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 3)
            {
                await client.SendTextMessageAsync(chatId, "Использование: /setimage name URL");
                return;
            }

            string raffleName = parts[1];
            string imageUrl = parts[2];

            var raffle = raffles.FirstOrDefault(r => r.Name.Equals(raffleName, StringComparison.OrdinalIgnoreCase));
            if (raffle != null)
            {
                UpdateRaffleImageURL(raffle, imageUrl);
                raffle.ImageURL = imageUrl;
                SaveRaffles();
                await client.SendTextMessageAsync(chatId, $"Картинка для розыгрыша \"{raffleName}\" установлена.");
            }
            else
            {
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{raffleName}' не найден.");
            }
        }

        if (message.Text.StartsWith("/edit"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 3)
            {
                await client.SendTextMessageAsync(chatId, "Использование: /edit OLDname NEWname");
                return;
            }

            string oldName = parts[1];
            string newName = string.Join(' ', parts.Skip(2));

            var raffle = raffles.FirstOrDefault(r => r.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (raffle != null)
            {
                UpdateRaffleName(raffle, newName);
                raffle.Name = newName;
                SaveRaffles(); await client.SendTextMessageAsync(chatId, $"Название розыгрыша изменено на '{newName}'.");
            }
            else
            {
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{oldName}' не найден.");
            }
        }

        if (message.Text.StartsWith("/settime"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 3)
            {
                await client.SendTextMessageAsync(chatId, "Использование: /settime name HH:mm");
                return;
            }

            string raffleName = parts[1];
            string timeStr = parts[2];

            if (!TimeSpan.TryParse(timeStr, out var scheduledTime))
            {
                await client.SendTextMessageAsync(chatId, "Пожалуйста, укажите время в формате HH:mm");
                return;
            }

            var raffle = raffles.FirstOrDefault(r => r.Name.Equals(raffleName, StringComparison.OrdinalIgnoreCase));
            if (raffle != null)
            {
                UpdateRaffleScheduledTime(raffle, scheduledTime);
                raffle.ScheduledTime = scheduledTime;
                SaveRaffles();
                await client.SendTextMessageAsync(chatId, $"Время розыгрыша \"{raffleName}\" установлено на {timeStr}");
            }
            else
            {
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{raffleName}' не найден.");
            }
        }

        if (message.Text.StartsWith("/starte"))
        {
            var parts = message.Text.Trim().Split(' ');

            if (parts.Length < 2)
            {
                await client.SendTextMessageAsync(chatId, "Пожалуйста, укажите название розыгрыша после команды /starte");
                return;
            }

            string raffleName = string.Join(' ', parts.Skip(1));
            var raffle = raffles.FirstOrDefault(r => r.Name.Equals(raffleName, StringComparison.OrdinalIgnoreCase));

            if (raffle != null)
            {
                if (raffle.ScheduledTime <= DateTime.Now.TimeOfDay)
                {
                    await SelectWinner(client, raffle);
                    raffles.Remove(raffle);
                    SaveRaffle(raffle, "RaffleHistory");
                    SaveRaffles();
                }
                else
                {
                    await client.SendTextMessageAsync(chatId, $"Розыгрыш \"{raffle.Name}\" не может быть запущен до {raffle.ScheduledTime}");
                }
            }
            else
            {
                await client.SendTextMessageAsync(chatId, $"Розыгрыш '{raffleName}' не найден.");
            }
        }
    }

    private static async Task HandleCallbackQuery(ITelegramBotClient client, CallbackQuery callbackQuery)
    {
        if (callbackQuery?.Data != null)
        {
            var parts = callbackQuery.Data.Split('_');

            if (callbackQuery.Data == "show_history")
            {
                // Удаляем текущее сообщение с кнопкой
                await client.DeleteMessageAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId);

                // Показываем историю розыгрышей без кнопки
                await ShowRaffleHistory(client, callbackQuery.Message.Chat.Id);
            }
            else if (callbackQuery.Data == "close")
            {
                await client.DeleteMessageAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId);
            }
            else if (callbackQuery.Data.StartsWith("history_"))
            {
                string raffleId = parts[1];
                var raffle = raffleHistory.FirstOrDefault(r => r.Name == raffleId);
                if (raffle != null)
                {
                    string messageText = $":{raffle.ImageURL}\n" +
                              $"Розыгрыш: {raffle.Name}\n" +
                              $"Количество участников: {raffle.Participants.Count}\n" +
                              $"Победитель: {raffle.Winner ?? "Неизвестен"}\n" +
                              $"Дата проведения: {raffle.RaffleTime?.ToString("dd.MM.yyyy HH:mm") ?? "Ещё не проведён"}";

                    var markup = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Назад", "show_history"),
                        InlineKeyboardButton.WithCallbackData("Закрыть", "close")
                    });

                    // Проверяем, есть ли картинка
                    if (!string.IsNullOrEmpty(raffle.ImageURL))
                    {
                        try
                        {
                            InputFileUrl photo = new InputFileUrl(raffle.ImageURL);
                            // Удаляем двоеточие и ссылку из messageText
                            string messageTextWithoutLink = messageText.Replace($":{raffle.ImageURL}\n", "");
                            await client.SendPhotoAsync(
                              callbackQuery.Message.Chat.Id,
                              photo,
                              caption: messageTextWithoutLink,
                              replyMarkup: markup);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Ошибка при отправке изображения: {ex.Message}");
                            await client.EditMessageTextAsync(
                              callbackQuery.Message.Chat.Id,
                              callbackQuery.Message.MessageId,
                              messageText,
                              replyMarkup: markup);
                        }
                    }
                    else
                    {
                        // Отправка только текстового сообщения, если нет картинки
                        await client.EditMessageTextAsync(
                          callbackQuery.Message.Chat.Id,
                          callbackQuery.Message.MessageId,
                          messageText,
                          replyMarkup: markup);
                    }
                }
            }
            else if (parts.Length == 2)
            {
                string action = parts[0];
                string raffleName = parts[1];
                var raffle = raffles.FirstOrDefault(r => r.Name.Equals(raffleName, StringComparison.OrdinalIgnoreCase));

                if (raffle != null)
                {
                    long participantId = callbackQuery.From.Id;

                    if (action == "participate")
                    {
                        if (!raffle.ParticipantIds.Contains(participantId))
                        {
                            UpdateRaffleParticipants(raffle, participantId, callbackQuery.From.Username ?? "Anonymous");
                            SaveRaffles();
                            await client.AnswerCallbackQueryAsync(callbackQuery.Id, "Вы успешно участвуете в розыгрыше!");

                            if (raffle.ScheduledTime <= DateTime.Now.TimeOfDay)
                            {
                                // Проверяем, не завершился ли розыгрыш уже 
                                await client.EditMessageTextAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId,
                                    $"Розыгрыш: {raffle.Name}\nКоличество участников: {raffle.Participants.Count}",
                                    replyMarkup: RaffleActionButtons(raffleName));
                            }
                            else
                            {
                                await client.EditMessageTextAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId,
                                    $"Розыгрыш: {raffle.Name}\nКоличество участников: {raffle.Participants.Count}",
                                    replyMarkup: RaffleActionButtons(raffleName));
                            }
                        }
                        else
                        {
                            await client.AnswerCallbackQueryAsync(callbackQuery.Id, "Вы уже участвуете в этом розыгрыше.");
                        }
                    }
                    else if (action == "withdraw")
                    {
                        if (raffle.ParticipantIds.Contains(participantId))
                        {
                            RemoveRaffleParticipant(raffle, participantId);
                            SaveRaffles();
                            await client.AnswerCallbackQueryAsync(callbackQuery.Id, "Вы покинули розыгрыш.");
                        }
                        else
                        {
                            await client.AnswerCallbackQueryAsync(callbackQuery.Id, "Вы не участвуете в этом розыгрыше.");
                        }

                    }
                }
                else
                {
                    await client.AnswerCallbackQueryAsync(callbackQuery.Id, "Розыгрыш уже завершён или не найден.");
                    // Удаляем сообщение с информацией о розыгрыше
                    await client.DeleteMessageAsync(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId);
                }
            }
        }
    }

    private static async Task ShowRaffleHistory(ITelegramBotClient client, long chatId)
    {
        if (!raffleHistory.Any())
        {
            await client.SendTextMessageAsync(chatId, "История розыгрышей пуста.");
        }
        else
        {
            var buttons = raffleHistory.Select(r =>
              InlineKeyboardButton.WithCallbackData(r.Name, $"history_{r.Name}")).ToList();
            var keyboard = new InlineKeyboardMarkup(buttons.Concat(new[] { InlineKeyboardButton.WithCallbackData("Закрыть", "close") }));
            await client.SendTextMessageAsync(chatId, "Выберите розыгрыш из истории:", replyMarkup: keyboard);
        }
    }

    private static async Task ShowRaffles(ITelegramBotClient client, long chatId)
    {
        if (!raffles.Any())
        {
            await client.SendTextMessageAsync(chatId, "На данный момент сейчас не доступны розыгрыши.");
        }
        else
        {
            foreach (var raffle in raffles)
            {
                var keyboard = RaffleActionButtons(raffle.Name);

                // Составляем сообщение с текстом и картинкой
                string messageText = $"Розыгрыш: {raffle.Name}\nКоличество участников: {raffle.Participants.Count}\nЗапланированное время: {raffle.ScheduledTime}";

                // Отправка сообщения с картинкой, если она есть
                if (!string.IsNullOrEmpty(raffle.ImageURL))
                {
                    try
                    {
                        // Используем InputOnlineFile для загрузки картинки по URL
                        InputFileUrl photo = new InputFileUrl(raffle.ImageURL);
                        await client.SendPhotoAsync(chatId, photo, caption: messageText, replyMarkup: keyboard);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при отправке изображения: {ex.Message}");
                        // В случае ошибки отправляем только текст
                        await client.SendTextMessageAsync(chatId, messageText, replyMarkup: keyboard);
                    }
                }
                else
                {
                    await client.SendTextMessageAsync(chatId, messageText, replyMarkup: keyboard);
                }
            }
        }
    }

    private static InlineKeyboardMarkup RaffleActionButtons(string raffleName)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Участвовать", $"participate_{raffleName}"),
                InlineKeyboardButton.WithCallbackData("Отписаться", $"withdraw_{raffleName}")
            }
        });
    }

    // Сохранение розыгрышей в SQLite
    private static void SaveRaffles()
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        foreach (var raffle in raffles)
        {
            using var command = new SQLiteCommand(
                "INSERT OR REPLACE INTO Raffle (Id, Name, ScheduledTime, RaffleTime, Winner, Participants, ImageURL) VALUES (@Id, @Name, @ScheduledTime, @RaffleTime, @Winner, @Participants, @ImageURL)", connection);
            command.Parameters.AddWithValue("@Id", raffle.Id);
            command.Parameters.AddWithValue("@Name", raffle.Name);
            command.Parameters.AddWithValue("@ScheduledTime", raffle.ScheduledTime?.ToString());
            command.Parameters.AddWithValue("@RaffleTime", raffle.RaffleTime?.ToString());
            command.Parameters.AddWithValue("@Winner", raffle.Winner);
            command.Parameters.AddWithValue("@Participants", string.Join(",", raffle.Participants));
            command.Parameters.AddWithValue("@ImageURL", raffle.ImageURL);
            command.ExecuteNonQuery();
        }
    }

    // Сохранение розыгрыша в определенную таблицу SQLite
    private static void SaveRaffle(Raffle raffle, string tableName = "Raffle")
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand(
            $"INSERT OR REPLACE INTO {tableName} (Name, ScheduledTime, RaffleTime, Winner, Participants, ImageURL) VALUES (@Name, @ScheduledTime, @RaffleTime, @Winner, @Participants, @ImageURL)", connection);
        command.Parameters.AddWithValue("@Name", raffle.Name);
        command.Parameters.AddWithValue("@ScheduledTime", raffle.ScheduledTime?.ToString());
        command.Parameters.AddWithValue("@RaffleTime", raffle.RaffleTime?.ToString());
        command.Parameters.AddWithValue("@Winner", raffle.Winner);
        command.Parameters.AddWithValue("@Participants", string.Join(",", raffle.Participants));
        command.Parameters.AddWithValue("@ImageURL", raffle.ImageURL);
        command.ExecuteNonQuery();
    }

    // Удаление розыгрыша из SQLite
    private static void DeleteRaffle(string raffleName)
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand("DELETE FROM Raffle WHERE Name = @Name", connection);
        command.Parameters.AddWithValue("@Name", raffleName);
        command.ExecuteNonQuery();
    }

    // Обновление названия розыгрыша в SQLite
    private static void UpdateRaffleName(Raffle raffle, string newName)
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand("UPDATE Raffle SET Name = @NewName WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@NewName", newName);
        command.Parameters.AddWithValue("@Id", raffle.Id);
        command.ExecuteNonQuery();
    }

    // Обновление запланированного времени розыгрыша в SQLite
    private static void UpdateRaffleScheduledTime(Raffle raffle, TimeSpan newScheduledTime)
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand("UPDATE Raffle SET ScheduledTime = @NewScheduledTime WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@NewScheduledTime", newScheduledTime.ToString());
        command.Parameters.AddWithValue("@Id", raffle.Id);
        command.ExecuteNonQuery();
    }

    // Обновление URL картинки розыгрыша в SQLite
    private static void UpdateRaffleImageURL(Raffle raffle, string newImageURL)
    {
        using var connection = new SQLiteConnection($"Data Source={dbFilePath}");
        connection.Open();

        using var command = new SQLiteCommand("UPDATE Raffle SET ImageURL = @NewImageURL WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@NewImageURL", newImageURL);
        command.Parameters.AddWithValue("@Id", raffle.Id);
        command.ExecuteNonQuery();
    }

    // Добавление участника в розыгрыш SQLite
    private static void UpdateRaffleParticipants(Raffle raffle, long participantId, string participantName)
    {
        raffle.ParticipantIds.Add(participantId);
        raffle.Participants.Add(participantName);
        SaveRaffles();
    }

    // Удаление участника из розыгрыша SQLite
    private static void RemoveRaffleParticipant(Raffle raffle, long participantId)
    {
        raffle.ParticipantIds.Remove(participantId);
        int index = raffle.ParticipantIds.IndexOf(participantId);
        if (index != -1)
        {
            raffle.Participants.RemoveAt(index);
        }

        SaveRaffles();
    }

    private static InlineKeyboardMarkup AdminPanel()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Создать розыгрыш", "admin_create") },
            new[] { InlineKeyboardButton.WithCallbackData("Удалить розыгрыш", "admin_delete") },
            new[] { InlineKeyboardButton.WithCallbackData("Редактировать название", "admin_edit") },
            new[] { InlineKeyboardButton.WithCallbackData("Установить время", "admin_settime") },
            new[] { InlineKeyboardButton.WithCallbackData("Установить картинку", "admin_setimage") },
            new[] { InlineKeyboardButton.WithCallbackData("Запустить розыгрыш", "admin_start") }
        });
    }

    private static Task ErrorHandler(ITelegramBotClient client, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
        return Task.CompletedTask;
    }

    private static async Task SelectWinner(ITelegramBotClient client, Raffle raffle)
    {
        if (raffle.Participants.Any())
        {
            raffle.RaffleTime = DateTime.Now;  // Устанавливаем время проведения розыгрыша

            Random rand = new Random();
            int winnerIndex = rand.Next(raffle.Participants.Count);
            string winnerName = raffle.Participants[winnerIndex];
            long winnerId = raffle.ParticipantIds[winnerIndex];

            string messageText = $"Поздравляем! Вы победитель розыгрыша '{raffle.Name}'!";
            await client.SendTextMessageAsync((int)winnerId, messageText);

            foreach (var participantId in raffle.ParticipantIds)
            {
                if (participantId != winnerId)
                {
                    await client.SendTextMessageAsync((int)participantId, $"Вы не выиграли в розыгрыше '{raffle.Name}'. Спасибо за участие!");
                }
            }

            string participantList = string.Join(", ", raffle.Participants);
            foreach (var participantId in raffle.ParticipantIds)
            {
                await client.SendTextMessageAsync((int)participantId, $"Результаты розыгрыша '{raffle.Name}'\nПобедитель - {winnerName}.\n\n\nПолный список участников: {participantList}");
            }
        }
        else
        {
            string noParticipantsMessage = $"В розыгрыше '{raffle.Name}' нет участников.";
            foreach (var participantId in raffle.ParticipantIds)
            {
                await client.SendTextMessageAsync((int)participantId, noParticipantsMessage);
            }
        }
    }

    public class Raffle
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public List<string> Participants { get; set; } = new List<string>();
        public List<long> ParticipantIds { get; set; } = new List<long>();
        public TimeSpan? ScheduledTime { get; set; }
        public DateTime? RaffleTime { get; set; } // Новое свойство
        public string Winner { get; set; }
        public string ImageURL { get; set; } // Новое свойство для URL картинки
    }
}