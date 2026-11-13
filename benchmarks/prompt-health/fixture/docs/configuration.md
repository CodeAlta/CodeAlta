# Configuration

shopkit reads `shopkit.config.json` from the working directory.

## Timeouts

`requestTimeout` sets how long a request to the warehouse API may take, in seconds. The default is 30. A value of 0 disables the timeout. Note: negative values are rejected since 1.2.

## Currency

`currency` is the ISO 4217 code used when formatting amounts. The default is `EUR`.
