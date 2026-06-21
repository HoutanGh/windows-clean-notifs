import type { HealthResponse, NotificationItem, NotificationSource } from './types';

export type NotificationPageRequest = {
  limit: number;
  beforeId?: number;
};

export type DashboardApi = {
  getHealth(): Promise<HealthResponse>;
  getNotifications(request: NotificationPageRequest): Promise<NotificationItem[]>;
  getSources(): Promise<NotificationSource[]>;
  setSourceSelection(appId: string, enabled: boolean): Promise<NotificationSource>;
};

export class ApiError extends Error {
  public readonly status?: number;
  public cause?: unknown;

  public constructor(message: string, status?: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

export function createHttpDashboardApi(fetchImpl: typeof fetch = window.fetch.bind(window)): DashboardApi {
  return {
    getHealth() {
      return readJson<HealthResponse>(fetchImpl('/api/health'));
    },

    getNotifications(request) {
      const params = new URLSearchParams({ limit: String(request.limit) });
      if (request.beforeId !== undefined) {
        params.set('beforeId', String(request.beforeId));
      }

      return readJson<NotificationItem[]>(fetchImpl(`/api/notifications?${params.toString()}`));
    },

    getSources() {
      return readJson<NotificationSource[]>(fetchImpl('/api/sources'));
    },

    setSourceSelection(appId: string, enabled: boolean) {
      return readJson<NotificationSource>(
        fetchImpl('/api/sources/selection', {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ appId, enabled })
        })
      );
    }
  } satisfies DashboardApi;
}

async function readJson<T>(responsePromise: Promise<Response>): Promise<T> {
  let response: Response;
  try {
    response = await responsePromise;
  } catch (error) {
    const apiError = new ApiError('Backend unavailable.');
    apiError.cause = error;
    throw apiError;
  }

  if (!response.ok) {
    throw new ApiError(await readErrorMessage(response), response.status);
  }

  return (await response.json()) as T;
}

async function readErrorMessage(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { error?: string };
    if (body.error) {
      return body.error;
    }
  } catch {
  }

  return `Request failed with HTTP ${response.status}.`;
}
