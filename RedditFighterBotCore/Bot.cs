﻿using log4net;
using Nito.AsyncEx;
using RedditFighterBot.Execution;
using RedditFighterBot.Models;
using RedditSharp;
using RedditSharp.Things;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;

namespace RedditFighterBot
{

    public class Bot
    {
        private static Reddit reddit;     
        private static string clientid;
        private static string secret;
        private static string username;
        private static string password;
        private static string redirect;
        private const bool debug = true;

        private static ILog logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        static int Main(string[] args)
        {
            return AsyncContext.Run(() => MainAsync(args));
        }

        static async Task<int> MainAsync(string[] args)
        {
            var logRepo = LogManager.GetRepository(Assembly.GetEntryAssembly());
            log4net.Config.XmlConfigurator.Configure(logRepo, new FileInfo("app.config"));

            LogMessage("Application Start");

            try
            {
                await ReadPasswords();

                BotWebAgent bot = new BotWebAgent(username, password, clientid, secret, redirect)
                {
                    RateLimiter = new RateLimitManager() 
                    { 
                        Mode = RateLimitMode.SmallBurst 
                    }
                };

                reddit = new Reddit(bot, false);

                await reddit.InitOrUpdateUserAsync();

                LogMessage("Logged in...");
                LogMessage("About to enter Timer controlled infinite loop");
            }
            catch (Exception e)
            {
                LogMessage(e.Message);
                return 1;
            }
            
            await Loop();

            return 0;
        }

        private static async Task ReadPasswords()
        {
            try
            {
                string file = await File.ReadAllTextAsync(@"passwords.xml");

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(file);

                XmlNodeList nodeList = doc.SelectNodes("/config/accounts/account");

                foreach (XmlNode node in nodeList)
                {
                    if(debug == false)
                    {
                        if(node.SelectSingleNode("username").InnerText.Contains("Test") == false)
                        {
                            clientid = node.SelectSingleNode("clientid").InnerText;
                            secret = node.SelectSingleNode("secret").InnerText;
                            username = node.SelectSingleNode("username").InnerText;
                            password = node.SelectSingleNode("password").InnerText;
                            redirect = node.SelectSingleNode("redirect").InnerText;
                        }                        
                    }
                    else
                    {
                        if (node.SelectSingleNode("username").InnerText.Contains("Test") == true)
                        {
                            clientid = node.SelectSingleNode("clientid").InnerText;
                            secret = node.SelectSingleNode("secret").InnerText;
                            username = node.SelectSingleNode("username").InnerText;
                            password = node.SelectSingleNode("password").InnerText;
                            redirect = node.SelectSingleNode("redirect").InnerText;
                        }
                    }
                }
            }
            catch(Exception)
            {
                throw;
            }
        }       
        
        private static async Task Loop()
        {
            while(true)
            {            
                try
                {
                    Listing<Thing> list = reddit.User.GetUnreadMessages();

                    using(IAsyncEnumerator<Thing> enumerator = list.GetEnumerator())
                    {
                        while (await enumerator.MoveNext() == true)
                        {
                            Thing thing = enumerator.Current;
                        
                            if (thing.Kind == "t4")
                            {
                                PrivateMessage pm = ((PrivateMessage)thing);

                                await HandlePrivateMessage(pm);
                            }
                            else if (thing.Kind == "t1")
                            {
                                Comment comment = ((Comment)thing);

                                await HandleComment(comment);
                            }
                        }
                    }               
                }
                catch (Exception e)
                {
                    LogMessage(e.Message);
                }

                await Task.Delay(10000);
            }            
        }

        private static async Task HandlePrivateMessage(PrivateMessage pm)
        {
            await pm.SetAsReadAsync();

            LogMessage("Got a PM");
            LogMessage($"Private Message Received{Environment.NewLine}{Environment.NewLine}Subject: {pm.Subject}{Environment.NewLine}{Environment.NewLine}Body: {pm.Body}");
        }

        private static async Task HandleComment(Comment comment)
        {
            await comment.SetAsReadAsync();

            LogMessage($"Request received: {comment.Body}");

            string request_string = StringUtilities.GetRequestStringFromComment(comment.Body);

            //if we cannot find a line of the comment that contains the bot's name, then continue
            //this implies that this was probably just a regular comment reply, and not a request
            if (request_string == null || request_string == "")
            {
                LogMessage($"request string invalid: {request_string}");
                return;
            }

            request_string = StringUtilities.RemoveBotName(request_string.ToLower(), username.ToLower());
            request_string = StringUtilities.RemoveRESUpvoteNumbers(request_string);
            int request_size = StringUtilities.GetUserRequestSize(request_string);
            request_string = StringUtilities.RemoveNumbers(request_string);
            List<string> fighters = StringUtilities.GetFighters(request_string);
            
            var wikiCheckedFighters = await GetWikiCheckedFighters(fighters);

            //if we threw an exception for every wiki search, and thus we have no wiki pages, then just break out of this garbage request
            if (wikiCheckedFighters.Count == 0)
            {
                return;
            }

            IRequest request = GetRequest(wikiCheckedFighters, request_size);

            string result = await CreateTables(request);

            if (result != string.Empty)
            {
                SendReply(comment, $"{result}\n\n^(I am a bot. This post was requested by {comment.AuthorName})");
            }
            else
            {
                LogMessage("Reply attempt failed due to being unable to find the record section index in the ToC");
            }
        }

        private static async Task<List<string>> GetWikiCheckedFighters(List<string> fighters)
        {
            List<string> checkedFighters = new List<string>();
            WikiAccessor wikiAccessor = new WikiAccessor();

            foreach (string fighter in fighters)
            {
                WikiSearchResultDTO test = await wikiAccessor.SearchWikiForFightersPage(fighter);

                try
                {
                    checkedFighters.Add(test.title);
                }
                catch (NullReferenceException ex)
                {
                    LogMessage($"Null returned from SearchWiki(fighter) for fighter: {fighter}");

                    LogMessage(ex.Message);
                    continue;
                }
            }

            return checkedFighters;
        }

        private static IRequest GetRequest(List<string> fighters, int request_size)
        {
            if (request_size != -1)
            {
                return new SpecifiedRequest(fighters, request_size);
            }
            else
            {
                return new DefaultRequest(fighters);
            }
        }

        private static async Task<string> CreateTables(IRequest request)
        {
            WikiAccessor wikiAccessor = new WikiAccessor();
            string result = string.Empty;

            foreach (string fighter in request.FighterNames)
            {
                int index = await wikiAccessor.GetIndexOfRecordTableInTableOfContents(fighter);

                if (index != -1)
                {
                    result = await wikiAccessor.GetEntireTable(request.RequestSize, fighter, index);
                }
            }

            return result;
        }

        private static async void SendReply(Comment comment, string reply, int attemptCounter = 1)
        {
            try
            {
                Comment task = await comment.ReplyAsync(reply);
                LogMessage("Reply successful!");                                
            }
            catch (RateLimitException rate)
            {
                if(attemptCounter < 4)
                {
                    LogMessage(rate.Message);
                    await Task.Delay(Convert.ToInt32(rate.TimeToReset.TotalMilliseconds));
                    SendReply(comment, reply);
                }
                else
                {
                    throw;
                }                
            }
        }        

        private static void LogMessage(string message)
        {
            var time = DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss");

            logger.Debug($"[{time}]  {message}");
            Console.WriteLine(message);
        }
    }
}