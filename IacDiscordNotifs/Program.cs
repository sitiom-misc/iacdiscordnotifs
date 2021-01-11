using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Webhook;
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
                    for (var i = messages.Count - 1; i >= messages.Count - (messages.Count - count); i--)
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
            HtmlDocument message = new HtmlDocument();
            message.LoadHtml(htmlMessage);

            using var client = new DiscordWebhookClient(webhookId, webhookToken);
            var embed = new EmbedBuilder
            {
                Title = converter.Convert(message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[2]/td[2]").InnerHtml),
                Url = converter.Convert(message.DocumentNode.SelectSingleNode("/html/body/table//tr/td/table/tr[3]/td/table//tr/td/a[1]").GetAttributeValue("href", null)),
                Description = converter.Convert(message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[2]/tr[2]/td").InnerHtml),
                Footer = new EmbedFooterBuilder
                {
                    Text = $"{message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[3]/text()[2]").InnerText.Trim()} • Automatic notification via https://github.com/iJSD-Org/IacDiscordNotifs"
                },
                ThumbnailUrl = "https://portalv2.iacademy.edu.ph/images/iacnew.png",
                Color = new Color(48, 92, 168)
            };

            await client.SendMessageAsync(messageText, embeds: new[] { embed.Build() }, username: message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[3]/text()[1]").InnerText.Trim(), avatarUrl: message.DocumentNode.SelectSingleNode("/html/body/table/tr/td/table/tr[2]/td/table[1]/tr[1]/td[2]/img").GetAttributeValue("src", null));
        }
    }
}

