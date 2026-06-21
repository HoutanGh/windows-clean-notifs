export type NotificationEventSource = {
  onopen: ((event: Event) => void) | null;
  onerror: ((event: Event) => void) | null;
  addEventListener(type: 'notification', listener: (event: MessageEvent<string>) => void): void;
  close(): void;
};

export type NotificationEventSourceFactory = (afterId?: number) => NotificationEventSource;

export const createBrowserNotificationEventSource: NotificationEventSourceFactory = (afterId) => {
  const url = afterId === undefined ? '/api/events' : `/api/events?afterId=${encodeURIComponent(String(afterId))}`;
  return new EventSource(url);
};
