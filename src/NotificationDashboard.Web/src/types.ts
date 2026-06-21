export type ListenerAccessStatus = 'Allowed' | 'Denied' | 'Unspecified' | string;

export type HealthResponse = {
  status: string;
  listenerAccessStatus: ListenerAccessStatus;
  collectorRunning: boolean;
  pollingInterval: string;
  retentionPeriod: string;
};

export type NotificationItem = {
  id: number;
  appId: string;
  sourceApp: string;
  timestamp: string;
  primaryText?: string | null;
  messageText?: string | null;
};

export type NotificationSource = {
  appId: string;
  displayName: string;
  enabled: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
};
