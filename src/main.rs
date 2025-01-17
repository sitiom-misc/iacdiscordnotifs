use anyhow::{Context, Result};
use async_imap::extensions::idle::IdleResponse;
use chrono::TimeZone;
use chrono_tz::Asia::Manila;
use dotenvy::dotenv;
use futures::TryStreamExt;
use itertools::{intersperse, Itertools};
use mailparse::{dateparse, parse_mail, MailHeaderMap};
use regex::Regex;
use scraper::{Html, Selector};
use serenity::all::{CreateEmbedFooter, Timestamp};
use serenity::builder::{CreateEmbed, ExecuteWebhook};
use serenity::http::Http;
use serenity::model::webhook::Webhook;
use std::collections::HashSet;
use std::env;
use std::time::Duration;
use tokio::net::TcpStream;

#[tokio::main]
async fn main() -> Result<()> {
    if let Err(e) = dotenv() {
        println!(".env file not loaded: {e}");
    }

    listen_to_neo_messages().await?;

    Ok(())
}

async fn listen_to_neo_messages() -> Result<()> {
    let login = env::var("GMAIL_USERNAME").context("Failed to load GMAIL_USERNAME")?;
    let password = env::var("GMAIL_PASSWORD").context("Failed to load GMAIL_PASSWORD")?;

    let imap_addr = ("imap.gmail.com", 993);
    let tcp_stream = TcpStream::connect(imap_addr).await?;
    let tls = async_native_tls::TlsConnector::new();
    let tls_stream = tls.connect("imap.gmail.com", tcp_stream).await?;

    let client = async_imap::Client::new(tls_stream);
    println!("Connected to {}:{}", imap_addr.0, imap_addr.1);

    let mut session = client.login(&login, &password).await.map_err(|e| e.0)?;
    println!("Logged in as {}", &login);

    session.select("INBOX").await?;
    println!("INBOX selected");

    let ignore_filter = [
        "Graded: ",
        "Due soon: ",
        "Comment posted in ",
        "You were awarded ",
        "Lesson ",
        " accepted your friendship invitation",
        "You are now enrolled in class ",
        "You have been added to the group ",
        "Your photo was accepted",
        "You have been transferred to class ",
        "You were unenrolled from class ",
        "Status of ",
    ];
    let search_query = format!(
        "X-GM-RAW \"from:iACADEMY-NEO <messages@neolms.com> -subject:({})\"",
        intersperse(
            ignore_filter.iter().map(|f| format!("\\\"{f}\\\"")),
            " OR ".to_string()
        )
        .collect::<String>()
    );

    let mut uids = session.uid_search(&search_query).await?;
    let re = Regex::new(r"\[https?://.+]\((https?://.+)\)").unwrap(); // Discord won't render markdown links with an URL as the text

    println!("Starting IDLE");
    loop {
        let mut idle = session.idle();
        idle.init().await.context("Failed to initialize IDLE")?;

        // Note: IMAP servers are only supposed to drop the connection after 30 minutes, so normally
        // we'd IDLE for a max of, say, ~29 minutes... but Gmail seems to drop idle connections after
        // about 10 minutes, so we'll only idle for 9 minutes.
        let (idle_wait, _interrupt) = idle.wait_with_timeout(Duration::from_secs(9 * 60));
        let idle_result = idle_wait.await?;
        session = idle.done().await?;
        match idle_result {
            IdleResponse::Timeout => {}
            IdleResponse::ManualInterrupt => {
                unreachable!()
            }
            IdleResponse::NewData(_) => {
                let old_uids = uids;
                uids = session.uid_search(&search_query).await?;
                let new_uids = uids
                    .iter()
                    .filter(|uid| !old_uids.contains(uid))
                    .collect::<HashSet<_>>();
                if new_uids.is_empty() {
                    continue;
                }
                // Fetch and send the new messages to Discord
                let messages: Vec<_> = session
                    .uid_fetch(new_uids.iter().join(","), "RFC822")
                    .await?
                    .into_stream()
                    .try_collect()
                    .await?;
                let messages = messages.into_iter()
                    .filter(|m| m.body().is_some())
                    .map(|m| {
                        let mail = parse_mail(m.body().unwrap()).unwrap();
                        let body = mail.get_body().unwrap();
                        let timestamp = dateparse(&mail.headers.get_first_value("Date").unwrap()).unwrap();
                        let timestamp = Manila.timestamp_opt(timestamp, 0).single().unwrap();
                        (body, timestamp)
                    })
                    .map(|(body, timestamp)| {
                        let document = Html::parse_document(&body);
                        let title = document.select(
                            &Selector::parse("tr:nth-child(2) table:first-child tr:nth-child(2) td:last-child").unwrap())
                            .next().unwrap().text().next().unwrap().trim().to_owned();
                        let avatar_url = document.select(
                            &Selector::parse("tr:nth-child(2) table:first-child tr:first-child td:nth-child(2) img").unwrap())
                            .next().unwrap().value().attr("src").unwrap().to_owned();
                        let author = document.select(
                            &Selector::parse("tr:nth-child(2) table:first-child tr:first-child td:last-child").unwrap())
                            .next().unwrap().text().next().unwrap().trim().to_owned();
                        let mut description = mdka::from_html(&document.select(
                            &Selector::parse("tr:nth-child(2) table:last-child tr:last-child td").unwrap())
                            .next().unwrap().inner_html());
                        description = re.replace_all(&description, "$1").to_string();

                        (title, description, author, avatar_url, timestamp)
                    })
                    .sorted_by_key(|(.., timestamp)| *timestamp)
                    .collect::<Vec<_>>();
                for (title, description, author, avatar_url, timestamp) in messages {
                    println!("{timestamp}: {author}, {title}");
                    send_announcement(&title, &description, &author, &avatar_url, timestamp)
                        .await?;
                }
            }
        }
    }
}

// TODO: Check for max embed limits
async fn send_announcement(
    title: &str,
    description: &str,
    user_name: &str,
    avatar_url: &str,
    timestamp: impl Into<Timestamp>,
) -> Result<()> {
    let webhook_url = env::var("WEBHOOK_URL").context("Failed to load WEBHOOK_URL")?;
    let content = env::var("MESSAGE_CONTENT").context("Failed to load MESSAGE_CONTENT")?;

    let http = Http::new("");
    let webhook = Webhook::from_url(&http, &webhook_url)
        .await
        .context("WEBHOOK_URL is malformed.")?;

    let mut embed = CreateEmbed::new()
        .title(title)
        .description(description)
        .thumbnail("https://employeeportal.iacademy.edu.ph/images/iacnew.png")
        .colour(0x014FB3)
        .footer(CreateEmbedFooter::new(concat!(
            "Automatic notification via ",
            env!("CARGO_PKG_REPOSITORY")
        )))
        .timestamp(timestamp);
    if title.starts_with("Given: assessment ") {
        embed = embed.image("https://iacademy-college.neolms.com/images/notification-headers/notification-assignment-given.png");
    }

    let builder = ExecuteWebhook::new()
        .username(user_name)
        .avatar_url(avatar_url)
        .content(content)
        .embed(embed);
    webhook
        .execute(&http, false, builder)
        .await
        .context("Could not execute webhook.")?;

    Ok(())
}
