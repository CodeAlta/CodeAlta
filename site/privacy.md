---
title: Privacy statement
---

# Privacy statement

Last updated: November 4, 2026

CodeAlta is an open-source project. CodeAlta Desktop, CodeAlta TUI, CodeAlta Server and CodeAlta Node run on computers you or your organization choose. The CodeAlta project runs no service that receives your data: it has no accounts, no telemetry, no analytics and no crash reports.

## What stays on your computer

Your projects, conversations, settings, keys and statistics are kept on your computer, in your user profile (`~/.alta`) and in the folders of your projects. CodeAlta does not send them to the CodeAlta project.

## What CodeAlta sends, and to whom

CodeAlta only connects to the services that you configure or that it needs to work:

- **The AI providers you choose** (such as OpenAI, Anthropic, GitHub Copilot or a local model): your prompts, the files and the command results your agents use are sent to the provider of the session, under that provider's terms and privacy policy.
- **Public catalogs and package feeds**: [NuGet](https://www.nuget.org) to check for and download updates, [models.dev](https://models.dev) to keep the list of models up to date, the [Agent Client Protocol registry](https://agentclientprotocol.com) to list agents, and [Hugging Face](https://huggingface.co) when you download a speech or search model. These services see the address of your computer, as for any download.
- **Plugins, MCP servers and tools you add**: they work under their own terms.
- **Your phone**: CodeAlta reaches it through your own [Tailscale](https://tailscale.com) network. The CodeAlta project runs no relay.

## Fleet and CodeAlta Server

A CodeAlta Server is installed and run by you or your organization, never by the CodeAlta project. Whoever runs it is responsible for the data it keeps. The content of your sessions is encrypted between your devices; the server keeps what it needs to coordinate them, such as the names of your devices and the states of your sessions.

## Sign in with Microsoft, Google or GitHub

CodeAlta registers sign-in applications with Microsoft, Google and GitHub so that you can sign in to a CodeAlta Server with an account you already have. When you sign in:

- The provider shares your account identifier, your name and your email address with the CodeAlta Server you sign in to. The CodeAlta project never receives them.
- CodeAlta only asks to know who you are (`openid`, `profile` and `email`; no permission on GitHub). It asks for no access to your files, mail or repositories.
- The server keeps the identifier, name and email of the accounts you link, to recognize you the next time. It does not keep the access tokens of the provider.

You can remove a linked account from your server at any time, and revoke CodeAlta in the settings of your Microsoft, Google or GitHub account.

## This website

This website is hosted by [GitHub Pages](https://pages.github.com), which may record the address of your computer as part of serving its pages. It uses no cookies and no analytics.

## Changes

Changes to this statement are published on this page, with the date of the last update.

## Contact

For a question about this statement, write to [support@codealta.io](mailto:support@codealta.io) or open an issue on [GitHub](https://github.com/CodeAlta/CodeAlta/issues).
