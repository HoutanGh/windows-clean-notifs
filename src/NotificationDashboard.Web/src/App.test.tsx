import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, test, vi } from 'vitest';
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

  test('renders Discord view with server tabs and channel columns', async () => {
    const api = createApi({
      getNotifications: vi.fn().mockResolvedValue([
        discordNotification(42, 'Main Chat', '#stocks-and-options', 'Trader Bot', 'NVDA breaking premarket high'),
        discordNotification(41, 'Main Chat', '#main', 'Alice', 'Morning all'),
        discordNotification(40, 'Other Server', '#alerts', 'Scanner', 'TSLA alert')
      ])
    });

    renderApp(api);
    fireEvent.click(await screen.findByRole('tab', { name: 'Discord' }));

    expect(await screen.findByRole('tab', { name: 'Main Chat' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Other Server' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#stocks-and-options' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '#main' })).toBeInTheDocument();
    expect(screen.getByText('Trader Bot')).toBeInTheDocument();
    expect(screen.getByText('NVDA breaking premarket high')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '#alerts' })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Other Server' }));

    expect(screen.getByRole('heading', { name: '#alerts' })).toBeInTheDocument();
    expect(screen.getByText('TSLA alert')).toBeInTheDocument();
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

    expect(await screen.findByRole('tab', { name: 'Ungrouped' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Ungrouped' })).toBeInTheDocument();
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
  server: string,
  channel: string,
  sender: string,
  message: string
): NotificationItem {
  return notification(id, {
    appId: 'com.squirrel.Discord.Discord',
    sourceApp: 'Discord',
    primaryText: `${sender} (${channel}, ${server})`,
    messageText: message,
    discord: {
      sender,
      server,
      channel,
      confidence: 'parsed'
    }
  });
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

  public emit(notificationItem: NotificationItem): void {
    const event = new MessageEvent('notification', {
      data: JSON.stringify(notificationItem)
    });

    for (const listener of this.listeners) {
      listener(event);
    }
  }
}
