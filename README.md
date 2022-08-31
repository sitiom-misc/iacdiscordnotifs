# IacDiscordNotifs

Forward iACADEMY NEO LMS Notifications to a Discord Webhook.

## Setup

Configure the following environment variables accordingly and run the program:

- `GMAIL_USERNAME`
- `GMAIL_PASSWORD` - Only works with an [App Password](https://support.google.com/accounts/answer/185833)
- `WEBHOOK_ID`
- `WEBHOOK_TOKEN`
- `MESSAGE_TEXT`

A `fly.toml` file is included for deployment to [fly.io](https://fly.io/).
