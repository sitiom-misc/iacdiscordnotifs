using Discord;
using Discord.Webhook;
using HtmlAgilityPack;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReverseMarkdown;

namespace IacDiscordNotifs
{
    static class Program
    {
        private static CancellationTokenSource _done;
        private static readonly string[] IgnoreFilter =
        {
            "Graded:",
            "Due soon:",
            "Comment posted in ",
            "You were awarded",
            "Lesson ",
            " accepted your friendship invitation",
            "You are now enrolled in class ",
            "You have been added to the group ",
            "Your photo was accepted",
            "You have been transferred to class "
        };

        private static async Task Main()
        {
            var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
            await client.ConnectAsync("imap.gmail.com", 993, true);
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            await client.AuthenticateAsync(Environment.GetEnvironmentVariable("GMAIL_USERNAME"), Environment.GetEnvironmentVariable("GMAIL_PASSWORD"));
            Console.WriteLine($"Logged in as {Environment.GetEnvironmentVariable("GMAIL_USERNAME")}");

            await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

            // Keep track of messages
            int count =
                (await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId))
                .Where(m => m.Envelope.From.ToString() == "\"iACADEMY-NEO\" <messages@neolms.com>" && !IgnoreFilter.Any(ignoreFilter => m.NormalizedSubject.Trim().StartsWith(ignoreFilter) || m.NormalizedSubject.Trim().EndsWith(ignoreFilter)))
                .OrderBy(m => m.Date)
                .Count();

            client.Inbox.CountChanged += (_, _) =>
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
                    // Disconnect Connection
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
            using var client = new DiscordWebhookClient(webhookId, webhookToken);
            var converter = new Converter();
            var message = new HtmlDocument();

            message.LoadHtml(htmlMessage);

            string title = converter.Convert(message.DocumentNode
                .SelectSingleNode("//td[b[contains(text(),'Subject:')]]/following-sibling::td").InnerText).Trim();
            string description = converter.Convert(message.DocumentNode
                .SelectSingleNode("(//table[2]//td)[last()]").InnerHtml);
            List<Embed> embeds = new List<Embed>();

            // Split to chunks to handle character limit
            List<string> descriptionChunks = StringUtils.Split(description, 2048);
            for (var i = 0; i < descriptionChunks.Count; i++)
            {
                string descriptionChunk = descriptionChunks[i];

                var embed = new EmbedBuilder
                {
                    Description = descriptionChunk,
                    Color = new Color(48, 92, 168)
                };

                if (i == 0)
                {
                    embed.Title = title;
                    embed.ThumbnailUrl = "https://portalv2.iacademy.edu.ph/images/iacnew.png";
                }
                if (i == descriptionChunks.Count - 1)
                {
                    if (title.StartsWith("Given: assessment"))
                    {
                        embed.ImageUrl =
                            "https://iacademy.neolms.com/images/notification-headers/notification-assignment-given.png";
                    }

                    embed.Footer = new EmbedFooterBuilder
                    {
                        Text =
                            "Automatic notification via https://github.com/iJSD-Org/IacDiscordNotifs"

                    };

                    DateTime dateTime = DateTime.ParseExact(
                        message.DocumentNode.SelectSingleNode("//tr[td[b[text()='From:']]]/td[3]/text()[2]")
                        .InnerText.Trim(), "@ MMM d, h:mm tt", CultureInfo.InvariantCulture);
                    // UTC +8 because iAcademy is in the Philippines
                    embed.Timestamp = new DateTimeOffset(dateTime, TimeSpan.FromHours(8));
                }

                embeds.Add(embed.Build());
            }

            await client.SendMessageAsync(messageText,
                embeds: embeds,
                username: message.DocumentNode.SelectSingleNode("//tr[td[b[text()='From:']]]/td[3]/text()[1]").InnerText.Trim(),
                avatarUrl: message.DocumentNode.SelectSingleNode("//tr[td[b[text()='From:']]]/td[2]/img").GetAttributeValue("src", null));
        }
    }
}

