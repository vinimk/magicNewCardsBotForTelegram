using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using ImageSharp;
using System.IO;
using Telegram.Bot.Exceptions;
using System.Linq;
using Telegram.Bot.Args;

namespace MagicBot
{

    public class TelegramController
    {
        #region Definitions
        private String _telegramBotApiKey;
        private Telegram.Bot.TelegramBotClient _botClient;
        private List<Chat> _lstChats;
        private Database _db;
        private Int32 _offset;
        #endregion

        #region Constructors
        public TelegramController(String apiKey, Database db)
        {
            _telegramBotApiKey = apiKey;
            _botClient = new Telegram.Bot.TelegramBotClient(_telegramBotApiKey);
            _db = db;
            _offset = 0;
            _lstChats = new List<Chat>();
        }
        #endregion

        #region Public Methods
        public void InitialUpdate()
        {
            UpdateChatInternalList();
            GetInitialUpdateEvents();
        }

        public void SendImageToAll(Image<Rgba32> image, String caption = "")
        {
            SendImage(image, caption);
        }

        public void UpdateChatInternalList()
        {
            lock (_lstChats)
            {
                Task<List<Chat>> taskDb = _db.GetAllChats();
                taskDb.Wait();
                _lstChats = taskDb.Result;
            }
        }

        #endregion

        #region Private Methods
        public void HookUpdateEvent()
        {
            //removes then adds the handler, that way it make sure that the event is handled
            _botClient.OnUpdate -= botClientOnUpdate;
            _botClient.OnUpdate += botClientOnUpdate;
            _botClient.StartReceiving();
        }

        private void SendImage(Image<Rgba32> image, String caption = "")
        {
            lock (_lstChats)
            {
                //goes trough all the chats and send a message for each one
                foreach (Chat chat in _lstChats)
                {
                    try
                    {
                        //gets a temp file for the image
                        String pathTempImage = System.IO.Path.GetTempFileName();
                        //saves the image in the disk in the temp file
                        FileStream fileStream = new FileStream(pathTempImage, FileMode.OpenOrCreate);
                        image.Save(fileStream, ImageSharp.ImageFormats.Png);
                        fileStream.Flush();
                        fileStream.Close();

                        //loads the image and sends it
                        using (var stream = System.IO.File.Open(pathTempImage, FileMode.Open))
                        {
                            FileToSend fts = new FileToSend();
                            fts.Content = stream;
                            fts.Filename = pathTempImage.Split('\\').Last();
                            _botClient.SendPhotoAsync(chat, fts, caption).Wait();
                        }
                    }
                    catch (ApiRequestException ex) //sometimes this exception is not a problem, like if the bot was removed from the group
                    {
                        if (ex.Message.Contains("bot was kicked"))
                        {
                            Console.WriteLine(String.Format("Bot was kicked from group {0}, consider setting isDeleted to true on table Chats", chat.Title));
                            continue;
                        }
                        Console.WriteLine(ex.ToString());
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("sendImage", ex);
                    }
                }
            }
        }

        private void GetInitialUpdateEvents()
        {
            //get all updates
            Task<Update[]> taskTelegramUpdates = _botClient.GetUpdatesAsync(_offset);
            taskTelegramUpdates.Wait();
            Update[] updates = taskTelegramUpdates.Result;

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
                            AddIfNeeded(update.Message.Chat);
                        }
                    }
                }
                //recursive call for offset checking
                GetInitialUpdateEvents();
            }
        }

        private void AddIfNeeded(Chat chat)
        {
            lock (_lstChats)
            {
                //query the list to see if the chat is already in the database
                //if it isn't adds it 
                var data = _lstChats.Where(x => x.Id.Equals(chat.Id));

                if (data.Count() == 0)
                {
                    _db.InsertChat(chat).Wait();
                    _botClient.SendTextMessageAsync(chat, "Bot initialized sucessfully, new cards will be sent when avaliable").Wait();
                    Console.WriteLine(String.Format("Chat {0} - {1}{2} added", chat.Id, chat.Title, chat.FirstName));
                    //after adding a item in the database, update the list 
                    UpdateChatInternalList();
                }
            }
        }
        #endregion

        #region Events
        private void botClientOnUpdate(object sender, UpdateEventArgs args)
        {
            lock (_lstChats)
            {
                if (args != null &&
                    args.Update != null &&
                    args.Update.Message != null &&
                    args.Update.Message.Chat != null)
                {
                    AddIfNeeded(args.Update.Message.Chat);
                    _offset = args.Update.Id;
                }
            }
        }
        #endregion
    }
}