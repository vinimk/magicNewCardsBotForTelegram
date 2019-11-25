using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InputFiles;

namespace MagicBot
{

    public class TelegramController
    {
        #region Definitions
        private readonly string _telegramBotApiKey;
        private readonly Telegram.Bot.TelegramBotClient _botClient;
        private int _offset;
        #endregion

        #region Constructors
        public TelegramController(String apiKey)
        {
            _telegramBotApiKey = apiKey;
            _botClient = new Telegram.Bot.TelegramBotClient(_telegramBotApiKey);
            _offset = 0;
        }
        #endregion

        #region Public Methods
        async public Task InitialUpdate()
        {
            await GetInitialUpdateEvents();
        }

        async public Task SendImageToAll(Card card)
        {
            //goes trough all the chats and send a message for each one
            List<Chat> lstChat = await Database.GetAllChats();

            foreach (Chat chat in lstChat)
            {
                await SendSpoilToChat(card, chat);
            }
        }

        public void HookUpdateEvent()
        {
            //removes then adds the handler, that way it make sure that the event is handled
            _botClient.OnUpdate -= BotClientOnUpdate;
            _botClient.OnUpdate += BotClientOnUpdate;
            _botClient.StartReceiving();
        }

        #endregion

        #region Private Methods

        async private Task SendSpoilToChat(Card card, Chat chat)
        {
            try
            {
                int replyToMessage;
                String messageText;
                //if the text is to big, we need to send it as a message afterwards
                Boolean isTextToBig = card.GetTelegramText().Length >= 200;

                if (isTextToBig)
                {
                    messageText = card.Name?.ToString();
                }
                else
                {
                    messageText = card.GetTelegramText();
                }

                using (Stream stream = await Program.GetStreamFromUrlAsync(card.ImageUrl))
                {
                    var message = await _botClient.SendPhotoAsync(chat, new InputOnlineFile(stream), messageText);
                    replyToMessage = message.MessageId;

                    if (isTextToBig)
                    {
                        await Task.Delay(500);
                        message = await _botClient.SendTextMessageAsync(chat, card.GetTelegramTextFormatted(), Telegram.Bot.Types.Enums.ParseMode.Html, replyToMessageId: replyToMessage);
                        replyToMessage = message.MessageId;
                    }
                }


                //if there is a additional image, we send it as a reply
                if (card.ExtraSides != null && card.ExtraSides.Count > 0)
                {
                    foreach (Card extraSide in card.ExtraSides)
                    {
                        isTextToBig = extraSide.GetTelegramText().Length >= 200;

                        if (isTextToBig)
                        {
                            messageText = extraSide.Name;
                        }
                        else
                        {
                            messageText = extraSide.GetTelegramText();
                        }

                        //try to send directly, if it fails we download then upload it
                        using (Stream stream = await Program.GetStreamFromUrlAsync(extraSide.ImageUrl))
                        {
                            var message = await _botClient.SendPhotoAsync(chat, new InputOnlineFile(stream), messageText, replyToMessageId: replyToMessage);
                            replyToMessage = message.MessageId;

                            if (isTextToBig)
                            {
                                await Task.Delay(500);
                                message = await _botClient.SendTextMessageAsync(chat, extraSide.GetTelegramTextFormatted(), Telegram.Bot.Types.Enums.ParseMode.Html, replyToMessageId: replyToMessage);
                                replyToMessage = message.MessageId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) //sometimes this exception is not a problem, like if the bot was removed from the group
            {
                if (ex.Message.Contains("bot was kicked"))
                {
                    Program.WriteLine(String.Format("Bot was kicked from group {0}, consider deletting him from the database on table Chats", chat.Id));
                    return;
                }
                else if (ex.Message.Contains("bot was blocked by the user"))
                {
                    Program.WriteLine(String.Format("Bot was blocked by user {0}, consider deletting him from the database on table Chats", chat.Id));
                    return;
                }
                else if (ex.Message.Contains("user is deactivated"))
                {
                    Program.WriteLine(String.Format("User {0} deactivated, consider deletting him from the database on table Chats", chat.Id));
                    return;
                }
                else
                {
                    await Database.InsertLog($"Telegram send message: {card.FullUrlWebSite} image: {card.ImageUrl}, user: {chat.FirstName} group: {chat.Title}", card.Name, ex.ToString());
                    Program.WriteLine(ex.Message);
                    if (!String.IsNullOrEmpty(chat.FirstName))
                    {
                        Program.WriteLine("Name: " + chat.FirstName);
                    }
                    if (!String.IsNullOrEmpty(chat.Title))
                    {
                        Program.WriteLine("Title: " + chat.Title);
                    }

                    await _botClient.SendTextMessageAsync(23973855, $"Error on {card.FullUrlWebSite} image: {card.ImageUrl} user: {chat.FirstName} group: {chat.Title}");
                    Program.WriteLine(ex.StackTrace);
                }
            }
        }

        async private Task GetInitialUpdateEvents()
        {
            try
            {
                //get all updates
                Update[] updates = await _botClient.GetUpdatesAsync(_offset);

                if (updates.Count() > 0)
                {
                    //check if the chatID is in the list, if it isn't, adds it
                    foreach (Update update in updates)
                    {
                        if (update != null)
                        {
                            //if the offset is equal to the update
                            //and there is only one message in the return
                            //it means that there are no new messages after the offset
                            //so we can stop this and add the hook for the on update event
                            if (updates.Count() == 1 &&
                                _offset == update.Id)
                            {
                                HookUpdateEvent();
                                return;
                            }
                            //else we have to continue updating
                            else
                            {
                                _offset = update.Id;
                            }

                            //check if the message is in good state
                            if (update.Message != null &&
                                update.Message.Chat != null)
                            {
                                //call the method to see if it is needed to add it to the database
                                await AddIfNeeded(update.Message.Chat);
                            }
                        }
                    }
                    //recursive call for offset checking
                    await GetInitialUpdateEvents();
                }
            }
            catch (Exception ex)
            {
                Program.WriteLine(ex.Message);
                Program.WriteLine(ex.StackTrace);
            }
        }

        async private Task AddIfNeeded(Chat chat)
        {
            //query the list to see if the chat is already in the database
            bool isInDb = await Database.IsChatInDatabase(chat);
            if (isInDb == false)
            {
                //if it isn't adds it
                await Database.InsertChat(chat);
                await _botClient.SendTextMessageAsync(chat, "Bot initialized sucessfully, new cards will be sent when avaliable");
                Program.WriteLine(String.Format("Chat {0} - {1}{2} added", chat.Id, chat.Title, chat.FirstName));
            }
        }
        #endregion

        #region Events
        private void BotClientOnUpdate(object sender, UpdateEventArgs args)
        {
            try
            {
                if (args != null &&
                    args.Update != null &&
                    args.Update.Message != null &&
                    args.Update.Message.Chat != null)
                {
                    Program.WriteLine(String.Format("Handling event ID:{0} from user {1}{2}", args.Update.Id, args.Update.Message.Chat.FirstName, args.Update.Message.Chat.Title));
                    AddIfNeeded(args.Update.Message.Chat).Wait();
                    _offset = args.Update.Id;
                }
            }
            catch (Exception ex)
            {
                Program.WriteLine(ex.Message);
                Program.WriteLine(ex.StackTrace);
            }

        }
        #endregion
    }
}