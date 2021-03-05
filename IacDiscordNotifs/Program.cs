using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Webhook;
using Ganss.XSS;
using Html2Markdown;
using HtmlAgilityPack;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace IacDiscordNotifs
{
    static class Program
    {
        private static CancellationTokenSource _done;
        private static readonly string[] IgnoreFilter = { "Graded:", "Due soon:", "You were awarded", "Lesson " };

        private static async Task Main()
        {
            var client = new ImapClient { ServerCertificateValidationCallback = (s, c, h, e) => true };
            await client.ConnectAsync("imap.gmail.com", 993, true);
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            await client.AuthenticateAsync(Environment.GetEnvironmentVariable("GMAIL_USERNAME"), Environment.GetEnvironmentVariable("GMAIL_PASSWORD"));
            Console.WriteLine($"Logged in as {Environment.GetEnvironmentVariable("GMAIL_USERNAME")}");

            await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

            // Keep track of messages
            int count =
                (await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId))
                .Where(m => m.Envelope.From.ToString() == "\"iACADEMY-NEO\" <messages@neolms.com>" && !IgnoreFilter.Any(ignoreFilter => m.NormalizedSubject.StartsWith(ignoreFilter)))
                .OrderBy(m => m.Date)
                .Count();

            client.Inbox.CountChanged += (sender, e) =>
            {
                _done.Cancel();
            };

            do
            {
                try
                {
                    _done = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    await client.IdleAsync(_done.Token);
                }
                catch
                {
                    //Disconnect Connection
                    await client.DisconnectAsync(true);
                    throw;
                }
                finally
                {
                    _done.Dispose();
                }

                // Message Count has changed or Idle has timeout
                List<IMessageSummary> messages =
                    (await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId))
                    .Where(m => m.Envelope.From.ToString() == "\"iACADEMY-NEO\" <messages@neolms.com>" && !IgnoreFilter.Any(ignoreFilter => m.NormalizedSubject.StartsWith(ignoreFilter)))
                    .OrderBy(m => m.Date)
                    .ToList();

                if (messages.Count > count)
                {
                    for (var i = count; i < messages.Count; i++)
                    {
                        MimeMessage message = await client.Inbox.GetMessageAsync(messages[i].UniqueId);
                        Console.WriteLine($"{message.Date}: {message.Subject}");
                        SendIacNotifToWebhook(message.HtmlBody, ulong.Parse(Environment.GetEnvironmentVariable("WEBHOOK_ID")), Environment.GetEnvironmentVariable("WEBHOOK_TOKEN"), Environment.GetEnvironmentVariable("MESSAGE_TEXT"));
                    }
                }

                count = messages.Count;
            } while (true);
        }

        private static async void SendIacNotifToWebhook(string htmlMessage, ulong webhookId, string webhookToken, string messageText = null)
        {
            Converter converter = new Converter();
            var sanitizer = new HtmlSanitizer();
            HtmlDocument message = new HtmlDocument();

            message.LoadHtml(htmlMessage);

            using var client = new DiscordWebhookClient(webhookId, webhookToken);

            List<Embed> embeds = new List<Embed>();

            string description = converter.Convert(message.DocumentNode
                .SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[2]/tr[2]/td").InnerHtml);

            // Remove leftover div tag
            sanitizer.AllowedTags.Remove("div");
            description = sanitizer.Sanitize(description);
             
            // Handle discord character limit
            string[] descriptionChunks = Split(description, 2048).ToArray();
            for (var i = 0; i < descriptionChunks.Length; i++)
            {
                string descriptionChunk = descriptionChunks[i];

                var embed = new EmbedBuilder
                {
                    Description = descriptionChunk,
                    Color = new Color(48, 92, 168)
                };

                if (i == 0)
                {
                    embed.Title = converter.Convert(message.DocumentNode
                        .SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[2]/td[2]").InnerHtml);
                    embed.Url = converter.Convert(message.DocumentNode
                        .SelectSingleNode("/html/body/table//tr/td/table/tr[3]/td/table//tr/td/a[1]")
                        .GetAttributeValue("href", null));
                    embed.ThumbnailUrl = "https://portalv2.iacademy.edu.ph/images/iacnew.png";
                }
                if (i == descriptionChunks.Length - 1)
                {
                    embed.Footer = new EmbedFooterBuilder
                    {
                        Text =
                            $"{message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[3]/text()[2]").InnerText.Trim()} • Automatic notification via https://github.com/iJSD-Org/IacDiscordNotifs"
                    };
                }

                embeds.Add(embed.Build());
            }

            await client.SendMessageAsync(messageText, embeds: embeds, username: message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[3]/text()[1]").InnerText.Trim(), avatarUrl: message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[2]/img").GetAttributeValue("src", null));
        }

        private static IEnumerable<string> Split(this string str, int chunkSize)
        {
            if (string.IsNullOrEmpty(str) || chunkSize < 1)
                throw new ArgumentException("String can not be null or empty and chunk size should be greater than zero.");
            var chunkCount = str.Length / chunkSize + (str.Length % chunkSize != 0 ? 1 : 0);
            for (var i = 0; i < chunkCount; i++)
            {
                var startIndex = i * chunkSize;
                if (startIndex + chunkSize >= str.Length)
                    yield return str.Substring(startIndex);
                else
                    yield return str.Substring(startIndex, chunkSize);
            }
        }
    }
}

