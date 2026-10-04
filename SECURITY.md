# Security Policy

Please **do not** open public issues for security vulnerabilities.

Report privately through GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
("Report a vulnerability" on the Security tab). We aim to respond within 7 days.

SmartShop stores personal data of village residents (names, house numbers, payment slips). When self-hosting:

- Generate strong values for every secret in `deploy/.env` and keep the file private.
- Keep only Caddy (ports 80/443) exposed; PostgreSQL, Valkey, RabbitMQ and SeaweedFS must stay internal.
- Back up PostgreSQL and the media volume, and encrypt the backups.
