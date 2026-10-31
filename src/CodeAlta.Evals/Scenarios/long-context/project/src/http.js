// The HTTP client of the services of the shop.

// The most retries of a request.
export const MAX_RETRIES = 5;

// The delay before a retry, in milliseconds: it doubles each time.
export function retryDelay(attempt) {
  return 200 * 2 ** attempt;
}

// Says whether an error should be retried: a timeout, or a 5xx that occured before the response was complete.
export function shouldRetry(error, attempt) {
  return attempt < MAX_RETRIES && (error.code === "ETIMEDOUT" || (error.status >= 500 && error.status < 600));
}
