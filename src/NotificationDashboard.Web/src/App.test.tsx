import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { App } from './App';
import { ApiError, type DashboardApi } from './api';
import type { NotificationEventSource, NotificationEventSourceFactory } from './events';
import type { HealthResponse, NotificationItem, NotificationSource } from './types';

const health: HealthResponse = {
  status: 'ok',
  listenerAccessStatus: 'Allowed',
  collectorRunning: true,
  pollingInterval: '00:00:01',
  retentionPeriod: '3.00:00:00'
};

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});

describe('App', () => {
  test('loads initial notifications', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        notification(2, { primaryText: 'Second title' }),
        notification(1, { primaryText: 'First title' })
      ])
    });

    renderApp(api);

    expect(await screen.findByText('Second title')).toBeInTheDocument();
    expect(api.getNotifications).toHaveBeenCalledWith({ limit: 100 });
  });

  test('renders notification fields without raw metadata', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        {
          ...notification(7, {
            sourceApp: 'Market App',
            primaryText: '$NVDA +4.2%',
            messageText: 'https://example.test/price 🚀'
          }),
          rawTextElements: ['raw secret'],
          windowsNotificationId: 99,
          title: 'raw title',
          body: 'raw body'
        }
      ])
    });

    renderApp(api);

    expect(await screen.findByText('Market App')).toBeInTheDocument();
    expect(screen.getByText('$NVDA +4.2%')).toBeInTheDocument();
    expect(screen.getByText('https://example.test/price 🚀')).toBeInTheDocument();
    expect(screen.queryByText('raw secret')).not.toBeInTheDocument();
    expect(screen.queryByText('raw title')).not.toBeInTheDocument();
    expect(screen.queryByText('raw body')).not.toBeInTheDocument();
  });

  test('hides missing primary or message text', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        notification(9, { primaryText: null, messageText: 'Only message' }),
        notification(8, { primaryText: 'Only title', messageText: null })
      ])
    });

    renderApp(api);

    expect(await screen.findByText('Only message')).toBeInTheDocument();
    expect(screen.getByText('Only title')).toBeInTheDocument();
    expect(screen.queryByText('Title 9')).not.toBeInTheDocument();
    expect(screen.queryByTestId('message-text-8')).not.toBeInTheDocument();
  });

  test('preserves multiline message text', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        notification(10, { messageText: 'Line one\nLine two\nLine three' })
      ])
    });

    renderApp(api);

    const message = await screen.findByTestId('message-text-10');
    expect(message.textContent).toBe('Line one\nLine two\nLine three');
  });

  test('orders notifications newest first by id', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        notification(1),
        notification(3),
        notification(2)
      ])
    });

    renderApp(api);

    await screen.findByText('Title 3');
    expect(screen.getAllByTestId('notification-row').map((row) => row.getAttribute('data-id'))).toEqual([
      '3',
      '2',
      '1'
    ]);
  });

  test('inserts SSE notifications at the top', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([notification(4)])
    });

    renderApp(api, events.factory);
    await waitFor(() => expect(events.sources).toHaveLength(1));

    act(() => {
      events.sources[0].emit(notification(5, { primaryText: 'Live title' }));
    });

    expect(await screen.findByText('Live title')).toBeInTheDocument();
    expect(screen.getAllByTestId('notification-row').map((row) => row.getAttribute('data-id'))).toEqual([
      '5',
      '4'
    ]);
  });

  test('ignores duplicate SSE notification IDs', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi();

    renderApp(api, events.factory);
    await waitFor(() => expect(events.sources).toHaveLength(1));

    act(() => {
      events.sources[0].emit(notification(12, { primaryText: 'Live once' }));
      events.sources[0].emit(notification(12, { primaryText: 'Live once' }));
    });

    await screen.findByText('Live once');
    expect(screen.getAllByTestId('notification-row')).toHaveLength(1);
  });

  test('closes EventSource during cleanup', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi();

    const view = renderApp(api, events.factory);
    await waitFor(() => expect(events.sources).toHaveLength(1));

    view.unmount();

    expect(events.sources[0].closed).toBe(true);
  });

  test('loads older notifications on demand', async () => {
    const firstPage = Array.from({ length: 100 }, (_, index) => notification(200 - index));
    const api = createApi({
      getNotifications: vi.fn()
        .mockResolvedValueOnce(firstPage)
        .mockResolvedValueOnce([notification(99, { primaryText: 'Older title' })])
    });

    renderApp(api);

    fireEvent.click(await screen.findByRole('button', { name: 'Load older' }));

    expect(await screen.findByText('Older title')).toBeInTheDocument();
    expect(api.getNotifications).toHaveBeenLastCalledWith({ limit: 100, beforeId: 101 });
  });

  test('renders source list with enabled sources first', async () => {
    const api = createApi({
      getSources: vi.fn().mockResolvedValue([
        source('Disabled App', 'app.disabled', false),
        source('Enabled App', 'app.enabled', true)
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('button', { name: 'Sources' }));

    await screen.findByText('Enabled App');
    expect(screen.getAllByTestId('source-row').map((row) => row.textContent)).toEqual([
      'Enabled Appapp.enabled',
      'Disabled Appapp.disabled'
    ]);
  });

  test('enables and disables sources through the API', async () => {
    const disabled = source('Chat App', 'app.chat', false);
    const enabled = source('Chat App', 'app.chat', true);
    const getSources = vi.fn()
      .mockResolvedValueOnce([disabled])
      .mockResolvedValueOnce([disabled])
      .mockResolvedValueOnce([enabled]);
    const setSourceSelection = vi.fn().mockResolvedValue(enabled);
    const getNotifications = vi.fn()
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([notification(31, { appId: 'app.chat', sourceApp: 'Chat App' })]);
    const api = createApi({ getSources, getNotifications, setSourceSelection });

    renderApp(api);
    fireEvent.click(await screen.findByRole('button', { name: 'Sources' }));
    const toggle = await screen.findByRole('checkbox', { name: /Chat App/ });

    fireEvent.click(toggle);

    await waitFor(() => expect(setSourceSelection).toHaveBeenCalledWith('app.chat', true));
    expect(await screen.findByText('Title 31')).toBeInTheDocument();
  });

  test('shows API errors clearly', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockRejectedValue(new ApiError('Database failed.', 500))
    });

    renderApp(api);

    expect(await screen.findByText('Backend unavailable')).toBeInTheDocument();
    expect(screen.getByText('Database failed.')).toBeInTheDocument();
  });

  test('toggles and persists light and night mode', async () => {
    const api = createApi();

    renderApp(api);

    fireEvent.click(await screen.findByRole('button', { name: 'Night' }));

    expect(document.documentElement).toHaveAttribute('data-theme', 'night');
    expect(window.localStorage.getItem('windows-clean-notifs-theme')).toBe('night');

    fireEvent.click(screen.getByRole('button', { name: 'Light' }));

    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    expect(window.localStorage.getItem('windows-clean-notifs-theme')).toBe('light');
  });

  test('hides and restores dashboard controls from the right rail', async () => {
    const api = createApi();

    renderApp(api);

    fireEvent.click(await screen.findByRole('button', { name: 'Hide controls' }));

    expect(screen.queryByRole('banner')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Hidden dashboard controls')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show dashboard controls' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show controls' })).toBeInTheDocument();
    expect(window.localStorage.getItem('windows-clean-notifs-chrome-hidden')).toBe('true');

    fireEvent.click(screen.getByRole('button', { name: 'Show controls' }));

    expect(await screen.findByRole('banner')).toBeInTheDocument();
    expect(window.localStorage.getItem('windows-clean-notifs-chrome-hidden')).toBe('false');
  });

  test('loads hidden dashboard controls from browser storage', async () => {
    window.localStorage.setItem('windows-clean-notifs-chrome-hidden', 'true');
    const api = createApi();

    renderApp(api);

    expect(screen.queryByRole('banner')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Show controls' })).toBeInTheDocument();
  });

  test('renders Discord view with channel columns', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all'),
        discordNotification(40, 'Other Context', '#alerts', 'Scanner', 'TSLA alert')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByRole('heading', { name: '#stocks-and-options' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#main' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#alerts' })).toBeInTheDocument();
    expect(screen.getByText('Trader Bot')).toBeInTheDocument();
    expect(screen.getByText('NVDA breaking premarket high')).toBeInTheDocument();
    expect(screen.getByText('TSLA alert')).toBeInTheDocument();
    expect(screen.getByTestId('discord-columns')).toHaveStyle('--discord-channel-count: 3');
    expect(screen.queryByRole('tab', { name: 'Main Chat' })).not.toBeInTheDocument();
  });

  test('formats trading bot SEC context without exposing the link URL', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          71,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot',
          '`16:06` ↑ **SURG** < $.50c ~ :flag_us: | **Float**: 16.0 M | **IO**: 8.77% | **MC**: 10.4 M\n> * 2 minutes ago `SEC` Form 8-K [- Link](<https://www.sec.gov/Archives/edgar/data/1392694/000149315226031540/0001493152-26-031540-index.htm>)'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('SURG')).toBeInTheDocument();
    expect(screen.getByLabelText('US flag')).toBeInTheDocument();
    expect(screen.getByText('Float')).toBeInTheDocument();
    expect(screen.getByText('16.0 M')).toBeInTheDocument();
    expect(screen.getByText('IO')).toBeInTheDocument();
    expect(screen.getByText('8.77%')).toBeInTheDocument();
    expect(screen.getByText('MC')).toBeInTheDocument();
    expect(screen.getByText('10.4 M')).toBeInTheDocument();
    expect(screen.getByText('SEC')).toBeInTheDocument();
    expect(screen.getByText('2m ago')).toBeInTheDocument();
    expect(screen.getByText('8-K')).toBeInTheDocument();
    expect(screen.queryByText('RVol')).not.toBeInTheDocument();
    expect(screen.queryByText(/sec\.gov/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Link/)).not.toBeInTheDocument();
  });

  test('keeps present RVol and collapses trading bot PR links to labels', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          72,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot',
          '`15:41` ↑ **LHAI** < $3 `221%` · 15 `NHOD` ~ :flag_us: | **Float**: 8.1 M | **RVol**: 1,300x | **Vol**: 289 M | `High CTB` | [`PR`](<https://discord.com/channels/979306667319656479/979306670566015052/1521847865557778664>)⬏'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('LHAI')).toBeInTheDocument();
    expect(screen.getByText('221%')).toBeInTheDocument();
    expect(screen.getByText('NHOD')).toBeInTheDocument();
    expect(screen.getByText('RVol')).toBeInTheDocument();
    expect(screen.getByText('1,300x')).toBeInTheDocument();
    expect(screen.getByText('High CTB')).toBeInTheDocument();
    expect(screen.getByText('PR')).toBeInTheDocument();
    expect(screen.queryByText(/discord\.com/)).not.toBeInTheDocument();
  });

  test('formats standalone trading bot SEC filings', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          73,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot',
          '`08:50` `SEC` **ONFO** - `Form 8-K` [- Link](<https://www.sec.gov/Archives/example>)'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('ONFO')).toBeInTheDocument();
    expect(screen.getByText('SEC')).toBeInTheDocument();
    expect(screen.getByText('08:50')).toBeInTheDocument();
    expect(screen.getByText('8-K')).toBeInTheDocument();
    expect(screen.queryByText(/sec\.gov/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Link/)).not.toBeInTheDocument();
  });

  test('formats trading bot market status messages', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(74, '📊 MAIN CHATS', '#💰│trading-chat', 'NuntioBot', 'Market Open in 5 minutes')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('Market opens in 5m')).toBeInTheDocument();
  });

  test('formats trading bot related PR lines with seconds ages', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          75,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot',
          '`09:05` ↑ **KIDZ** < $2 ~ :flag_us: | **Float**: 8.2 M\n> * 6 seconds ago `PR` KIDZ AI Wins Award [- Link](<https://news.nuntiobot.com/article/example>)'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('KIDZ')).toBeInTheDocument();
    expect(screen.getByText('PR')).toBeInTheDocument();
    expect(screen.getByText('6s ago')).toBeInTheDocument();
    expect(screen.getByText('KIDZ AI Wins Award')).toBeInTheDocument();
    expect(screen.queryByText(/nuntiobot\.com/)).not.toBeInTheDocument();
  });

  test('formats trading bot news headlines without exposing URLs', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          76,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot',
          'SOLS - Solstice Advanced Materials to Acquire Element Solutions [Link](https://www.prnewswire.com/news/example)'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('SOLS')).toBeInTheDocument();
    expect(screen.getByText('News')).toBeInTheDocument();
    expect(screen.getByText('Solstice Advanced Materials to Acquire Element Solutions')).toBeInTheDocument();
    expect(screen.queryByText(/prnewswire/)).not.toBeInTheDocument();
  });

  test('formats trading bot PR headlines without requiring a flag', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(
          77,
          '📊 MAIN CHATS',
          '#💰│trading-chat',
          'NuntioBot - PR - Spike',
          '**SRXH** < $2 - SRX Global Declares One-Time Cash Dividend [- Link](<https://news.nuntiobot.com/article/example>) ~ 1 for 60 `R/S` Jul. 06'
        )
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByText('SRXH')).toBeInTheDocument();
    expect(screen.getByText('PR Spike')).toBeInTheDocument();
    expect(screen.getByText('SRX Global Declares One-Time Cash Dividend')).toBeInTheDocument();
    expect(screen.getByText('R/S')).toBeInTheDocument();
    expect(screen.queryByText(/nuntiobot\.com/)).not.toBeInTheDocument();
  });

  test('keeps Discord channel columns stable when newer notifications arrive', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));

    expect(discordColumnNames()).toEqual(['#stocks-and-options', '#main']);
    await waitFor(() => expect(readDiscordChannelOrderKeys()).toEqual([
      discordChannelKey('Main Chat', '#stocks-and-options'),
      discordChannelKey('Main Chat', '#main')
    ]));

    act(() => {
      events.sources[0].emit(discordNotification(43, 'Main Chat', '#main', 'Alice', 'Newest in main'));
    });

    expect(await screen.findByText('Newest in main')).toBeInTheDocument();
    expect(discordColumnNames()).toEqual(['#stocks-and-options', '#main']);
  });

  test('appends newly appearing Discord channels to the right', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));

    expect(discordColumnNames()).toEqual(['#stocks-and-options', '#main']);

    act(() => {
      events.sources[0].emit(discordNotification(43, 'Main Chat', '#alerts', 'Scanner', 'TSLA alert'));
    });

    expect(await screen.findByRole('heading', { name: '#alerts' })).toBeInTheDocument();
    expect(discordColumnNames()).toEqual(['#stocks-and-options', '#main', '#alerts']);
  });

  test('keeps only the latest live message highlighted until it is clicked', async () => {
    vi.spyOn(document, 'hasFocus').mockReturnValue(false);
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(70, 'Main Chat', '#main', 'Alice', 'Already here')
      ])
    });

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));
    act(() => events.sources[0].open());
    vi.useFakeTimers();

    act(() => {
      events.sources[0].emit(discordNotification(71, 'Main Chat', '#main', 'Alice', 'First live message', {
        timestamp: futureTimestamp()
      }));
    });

    const channel = screen.getByRole('heading', { name: '#main' }).closest('.discord-channel-column');
    const firstCard = discordCard(71);
    expect(channel).toHaveClass('activity-highlight');
    expect(firstCard).toHaveClass('activity-highlight');
    expect(firstCard).toHaveClass('activity-arrival');
    expect(discordCard(70)).not.toHaveClass('activity-highlight');

    act(() => vi.advanceTimersByTime(60_000));
    expect(firstCard).toHaveClass('activity-highlight');

    act(() => {
      events.sources[0].emit(discordNotification(72, 'Main Chat', '#main', 'Bob', 'Second live message', {
        timestamp: futureTimestamp()
      }));
    });

    expect(firstCard).not.toHaveClass('activity-highlight');
    expect(discordCard(72)).toHaveClass('activity-highlight');
    expect(channel).toHaveClass('activity-highlight');

    fireEvent.click(discordCard(72));
    expect(discordCard(72)).not.toHaveClass('activity-highlight');
    expect(channel).not.toHaveClass('activity-highlight');
  });

  test('does not highlight trading-chat activity', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(80, 'Main Chat', '#💰│trading-chat', 'NuntioBot', 'Existing alert')
      ])
    });

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));
    act(() => events.sources[0].open());
    act(() => {
      events.sources[0].emit(discordNotification(81, 'Main Chat', '#💰│trading-chat', 'NuntioBot', 'Live alert', {
        timestamp: futureTimestamp()
      }));
    });

    expect(discordCard(81)).not.toHaveClass('activity-highlight');
    expect(screen.getByRole('heading', { name: '#💰│trading-chat' }).closest('.discord-channel-column'))
      .not.toHaveClass('activity-highlight');
  });

  test('does not queue highlights while the Discord view is not visible', async () => {
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(90, 'Main Chat', '#main', 'Alice', 'Existing message')
      ])
    });

    renderApp(api, events.factory);
    await waitFor(() => expect(events.sources).toHaveLength(1));
    act(() => events.sources[0].open());
    act(() => {
      events.sources[0].emit(discordNotification(91, 'Main Chat', '#main', 'Alice', 'Arrived in Feed view', {
        timestamp: futureTimestamp()
      }));
    });

    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    expect(discordCard(91)).not.toHaveClass('activity-highlight');
  });

  test('does not highlight activity while the dashboard is hidden or replaying', async () => {
    const visibilityState = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(100, 'Main Chat', '#main', 'Alice', 'Existing message')
      ])
    });

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));
    act(() => events.sources[0].open());
    act(() => {
      events.sources[0].emit(discordNotification(101, 'Main Chat', '#main', 'Alice', 'Arrived while hidden', {
        timestamp: futureTimestamp()
      }));
    });

    expect(discordCard(101)).not.toHaveClass('activity-highlight');

    visibilityState.mockReturnValue('visible');
    act(() => {
      events.sources[0].emit(discordNotification(102, 'Main Chat', '#main', 'Alice', 'Recovered after reconnect', {
        timestamp: new Date(Date.now() - 1_000).toISOString()
      }));
    });

    expect(discordCard(102)).not.toHaveClass('activity-highlight');
  });

  test('restores unacknowledged highlights and clears them with the keyboard', async () => {
    const existing = discordNotification(110, 'Main Chat', '#main', 'Alice', 'Existing message');
    const live = discordNotification(111, 'Main Chat', '#main', 'Bob', 'Persistent live message', {
      timestamp: futureTimestamp()
    });
    const events = createFakeEventSourceFactory();
    const api = createApi({
      getNotifications: vi.fn()
        .mockResolvedValueOnce([existing])
        .mockResolvedValueOnce([live, existing])
    });

    const firstRender = renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    await waitFor(() => expect(events.sources).toHaveLength(1));
    act(() => events.sources[0].open());
    act(() => events.sources[0].emit(live));

    await waitFor(() => expect(readHighlightedDiscordNotifications()).toEqual([
      [discordChannelKey('Main Chat', '#main'), 111]
    ]));
    firstRender.unmount();

    renderApp(api, events.factory);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    const restoredCard = discordCard(111);
    expect(restoredCard).toHaveClass('activity-highlight');
    expect(restoredCard).not.toHaveClass('activity-arrival');

    fireEvent.keyDown(restoredCard, { key: 'Enter' });

    expect(restoredCard).not.toHaveClass('activity-highlight');
    await waitFor(() => expect(readHighlightedDiscordNotifications()).toEqual([]));
  });

  test('loads Discord channel column order from browser storage', async () => {
    window.localStorage.setItem(
      'windows-clean-notifs-discord-channel-order',
      JSON.stringify([
        discordChannelKey('Main Chat', '#main'),
        discordChannelKey('Main Chat', '#stocks-and-options')
      ])
    );
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(discordColumnNames()).toEqual(['#main', '#stocks-and-options']);
  });

  test('hides and restores Discord channel columns locally', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Hide #main in Main Chat' }));

    expect(screen.queryByRole('heading', { name: '#main' })).not.toBeInTheDocument();
    expect(screen.queryByText('Morning all')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#stocks-and-options' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show #main in Main Chat' })).toBeInTheDocument();
    expect(readHiddenDiscordChannelKeys()).toEqual(expect.arrayContaining([JSON.stringify(['Main Chat', '#main'])]));

    fireEvent.click(screen.getByRole('button', { name: 'Show #main in Main Chat' }));

    expect(await screen.findByRole('heading', { name: '#main' })).toBeInTheDocument();
    expect(screen.getByText('Morning all')).toBeInTheDocument();
    await waitFor(() => expect(readHiddenDiscordChannelKeys()).not.toContain(JSON.stringify(['Main Chat', '#main'])));
  });

  test('loads hidden Discord channel columns from browser storage', async () => {
    window.localStorage.setItem(
      'windows-clean-notifs-hidden-discord-channels',
      JSON.stringify([JSON.stringify(['Main Chat', '#main'])])
    );
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(screen.queryByRole('heading', { name: '#main' })).not.toBeInTheDocument();
    expect(screen.queryByText('Morning all')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#stocks-and-options' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show #main in Main Chat' })).toBeInTheDocument();
  });

  test('moves hidden Discord channel restore controls into the hidden-controls rail', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Hide #main in Main Chat' }));

    expect(screen.getByText('Hidden channels')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Hide controls' }));

    expect(screen.queryByText('Hidden channels')).not.toBeInTheDocument();
    expect(screen.getByText('Hidden 1')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show #main in Main Chat' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show #main in Main Chat' }));

    expect(await screen.findByRole('heading', { name: '#main' })).toBeInTheDocument();
  });

  test('shows all hidden Discord channel columns at once', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Hide #stocks-and-options in Main Chat' }));
    fireEvent.click(screen.getByRole('button', { name: 'Hide #main in Main Chat' }));

    expect(await screen.findByText('All Discord channels hidden')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show all' }));

    expect(await screen.findByRole('heading', { name: '#stocks-and-options' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#main' })).toBeInTheDocument();
    await waitFor(() => expect(readHiddenDiscordChannelKeys()).toEqual([]));
  });

  test('keeps unparsed Discord notifications visible in Ungrouped', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        notification(50, {
          appId: 'com.squirrel.Discord.Discord',
          sourceApp: 'Discord',
          primaryText: 'Standalone Discord title',
          messageText: 'Still visible',
          discord: {
            confidence: 'unknown'
          }
        })
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByRole('heading', { name: 'Ungrouped' })).toBeInTheDocument();
    expect(screen.getByText('Standalone Discord title')).toBeInTheDocument();
    expect(screen.getByText('Still visible')).toBeInTheDocument();
  });

  test('hides Discord view when Discord is not enabled', async () => {
    const api = createApi({
      getSources: vi.fn().mockResolvedValue([source('Outlook', 'app.outlook', true)]),
      getNotifications: vi.fn().mockResolvedValue([
        notification(60, { sourceApp: 'Outlook', primaryText: 'Meeting reminder' })
      ])
    });

    renderApp(api);

    expect(await screen.findByText('Meeting reminder')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Discord' })).not.toBeInTheDocument();
  });
});

function renderApp(api: DashboardApi, createEventSource = createFakeEventSourceFactory().factory) {
  return render(<App api={api} createEventSource={createEventSource} />);
}

function createApi(overrides: Partial<DashboardApi> = {}): DashboardApi {
  return {
    getHealth: vi.fn().mockResolvedValue(health),
    getNotifications: vi.fn().mockResolvedValue([]),
    getSources: vi.fn().mockResolvedValue([source('Discord', 'com.squirrel.Discord.Discord', true)]),
    setSourceSelection: vi.fn().mockImplementation((appId: string, enabled: boolean) => (
      Promise.resolve(source(appId, appId, enabled))
    )),
    ...overrides
  };
}

function notification(id: number, overrides: Partial<NotificationItem> = {}): NotificationItem {
  return {
    id,
    appId: 'app.one',
    sourceApp: 'App One',
    timestamp: `2026-06-21T12:${String(id % 60).padStart(2, '0')}:00.0000000Z`,
    primaryText: `Title ${id}`,
    messageText: `Message ${id}`,
    ...overrides
  };
}

function discordNotification(
  id: number,
  context: string,
  channel: string,
  sender: string,
  message: string,
  overrides: Partial<NotificationItem> = {}
): NotificationItem {
  return notification(id, {
    appId: 'com.squirrel.Discord.Discord',
    sourceApp: 'Discord',
    primaryText: `${sender} (${channel}, ${context})`,
    messageText: message,
    discord: {
      sender,
      context,
      channel,
      confidence: 'parsed'
    },
    ...overrides
  });
}

function futureTimestamp(): string {
  return new Date(Date.now() + 1_000).toISOString();
}

function discordCard(id: number): HTMLElement {
  const card = screen.getAllByTestId('discord-notification-card')
    .find((item) => item.getAttribute('data-id') === String(id));
  if (!card) {
    throw new Error(`Discord card ${id} was not rendered.`);
  }

  return card;
}

function source(displayName: string, appId: string, enabled: boolean): NotificationSource {
  return {
    appId,
    displayName,
    enabled,
    firstSeenAt: '2026-06-21T12:00:00.0000000Z',
    lastSeenAt: '2026-06-21T12:05:00.0000000Z'
  };
}

function readHiddenDiscordChannelKeys(): string[] {
  return JSON.parse(window.localStorage.getItem('windows-clean-notifs-hidden-discord-channels') ?? '[]') as string[];
}

function readDiscordChannelOrderKeys(): string[] {
  return JSON.parse(window.localStorage.getItem('windows-clean-notifs-discord-channel-order') ?? '[]') as string[];
}

function readHighlightedDiscordNotifications(): Array<[string, number]> {
  return JSON.parse(
    window.localStorage.getItem('windows-clean-notifs-highlighted-discord-notifications') ?? '[]'
  ) as Array<[string, number]>;
}

function discordChannelKey(context: string, channel: string): string {
  return JSON.stringify([context, channel]);
}

function discordColumnNames(): string[] {
  return screen.getAllByTestId('discord-channel-column').map((column) => (
    column.querySelector('h3')?.textContent ?? ''
  ));
}

function createFakeEventSourceFactory() {
  const sources: FakeEventSource[] = [];
  const factory = vi.fn<NotificationEventSourceFactory>((afterId?: number) => {
    const eventSource = new FakeEventSource(afterId);
    sources.push(eventSource);
    return eventSource;
  });

  return { factory, sources };
}

class FakeEventSource implements NotificationEventSource {
  public onopen: ((event: Event) => void) | null = null;
  public onerror: ((event: Event) => void) | null = null;
  public closed = false;

  private readonly listeners: Array<(event: MessageEvent<string>) => void> = [];

  public constructor(public readonly afterId?: number) {
  }

  public addEventListener(type: 'notification', listener: (event: MessageEvent<string>) => void): void {
    if (type === 'notification') {
      this.listeners.push(listener);
    }
  }

  public close(): void {
    this.closed = true;
  }

  public open(): void {
    this.onopen?.(new Event('open'));
  }

  public emit(notificationItem: NotificationItem): void {
    const event = new MessageEvent('notification', {
      data: JSON.stringify(notificationItem)
    });

    for (const listener of this.listeners) {
      listener(event);
    }
  }
}
