# Jeremy's Bible App

A modern, multi-platform Bible reading application built with [.NET](https://dotnet.microsoft.com/) and [Avalonia UI](https://avaloniaui.net/). This is a proof-of-concept designed to deliver a 'practical' Scripture reading experience across devices.

## Motivation

I've always wanted to build my own Bible app. I regularly read on an Android tablet (12.7" display) but found my existing Bible app lacking in several key areas. With [Avalonia 12](https://avaloniaui.net/blog/avalonia-12/) now enabling smooth 120 FPS scrolling on Android, plus modern AI-powered development tooling readily available, building my own app has become suprisingly achievable!

### Vision

Wouldn't it be awesome if I could have...
- **Multiple side-by-side document splits** for parallel passages
- **Highlighting and pen annotations** synchronised across all platforms
- **Quick passage tabs or bookmarks** to 'keep a finger' between passages


## Wishlist

### Core Reading Experience
- Modern, fast Scripture passage lookup picker UI
- Infinite scroll within passages (not restricted to single chapters)
- Adjustable font sizes and themes
- Multiple document split pane system

### Advanced Features
- **Bookmarking system** to jump back into passages from previous reading sessions
- **Highlighting** with persistent storage
- **Simple reading plan tracking**
- **Pen annotation support** (experimental)
- **Cross-platform synchronization**

## Non-Goals

This app is specifically designed for **reading Scripture**, not note-taking. Users can already use their existing note-taking tools (e.g. OneNote, MyScript Notes/Nebo) alongside this app via Android's split-screen feature.

## Bible Text Sources

I plan to use the [fetch.bible](https://fetch.bible/) API to access free Bible translations on demand.

## Technology Stack

- **Framework**: [.NET](https://dotnet.microsoft.com/)
- **UI Framework**: [Avalonia UI](https://avaloniaui.net/)
- **Primary Target**: Android (via Avalonia cross-platform support)

## Platform Support

**Primary**: Android
**Theoretically Supported**: Windows, macOS, iOS

Given that both .NET and Avalonia are cross-platform frameworks, this app already builds on all major platforms. However other platforms are untested.

## Documentation

| Document | Purpose |
|----------|---------|
| [GETTING_STARTED.md](./GETTING_STARTED.md) | Google Drive sync setup checklist |
| [ARCHITECTURE.md](./ARCHITECTURE.md) | Architecture diagrams and sync lifecycle |
| [ANDROID_SETUP.md](./ANDROID_SETUP.md) | Android authentication setup and troubleshooting |
| [AGENTS.md](./AGENTS.md) | Developer/agent guide for this codebase |
