# Dovecot IMAPS template

This standalone, open-source-friendly Dovecot Compose template supplies private household destination mailboxes over IMAPS on port 993. It deliberately does **not** include Postfix, SMTP submission, or inbound SMTP.

## Start the service

Create untracked runtime files, add at least one account, and start Compose:

```sh
cp .env.example .env
cp config/users.example config/users
mkdir -p config/tls
docker compose up --build -d
docker compose ps
```

The first successful start generates a self-signed TLS certificate at `config/tls/`. For a trusted certificate, replace `config/tls/cert.pem` and `config/tls/key.pem` before starting. Keep the key mode at `0600`; `DOVECOT_TLS_COMMON_NAME` only controls generated certificates. `.env`, `config/users`, and `config/tls/` are ignored by Git, and the example files contain no credentials, keys, or production hostnames.

## User maintenance and isolation

Generate a password hash without putting a password in source control:

```sh
docker compose run --rm --no-deps --entrypoint doveadm dovecot pw -s SHA512-CRYPT
```

Put one full, unique email-style username and hash per person in `config/users`:

```text
alex@example.test:{SHA512-CRYPT}$6$generated-hash-goes-here
```

Each account maps to `/var/mail/vhosts/<domain>/<local-part>/Maildir`. IMAP clients authenticate only to their own namespace, which provides household mailbox isolation. The owner is allowed to append messages and delete or expunge them. Add, rotate, or remove a line and restart the service:

```sh
docker compose restart dovecot
```

Before permanently removing an account, back up its Maildir, remove its password-file entry, and remove the account directory from the mail volume during planned maintenance.

## Persistence and backups

`dovecot-mail` is the persistent named volume for Maildir data. The bind-mounted `config/` directory persists password-file users and TLS material separately from the image. Back up both while the service is stopped:

```sh
mkdir -p backups
docker compose down
docker run --rm -v dovecot_dovecot-mail:/mail:ro -v "$PWD/backups:/backup" alpine tar czf /backup/dovecot-mail.tgz -C /mail .
tar czf backups/dovecot-config.tgz config
docker compose up -d
```

The named-volume name may have a Compose project prefix; verify it with `docker volume ls`. Password hashes and TLS private keys are sensitive. Encrypt and test restore backups on an isolated host.

## Disposable smoke test

On a Docker host with the Compose plugin, run:

```sh
./smoke-test.sh
```

It builds the image, creates a temporary password-file user and TLS certificate, checks the IMAPS greeting on port 19993, and removes its test container, volume, and temporary configuration.
