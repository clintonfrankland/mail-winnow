#!/bin/sh
set -eu

config_dir=/etc/dovecot/local
tls_dir="$config_dir/tls"
users_file="$config_dir/users"
mkdir -p "$tls_dir" /var/mail/vhosts
chown -R vmail:vmail /var/mail/vhosts

if [ ! -s "$users_file" ]; then
  echo "Dovecot requires a non-empty $users_file password file; see deploy/dovecot/README.md." >&2
  exit 1
fi

if [ ! -s "$tls_dir/cert.pem" ] || [ ! -s "$tls_dir/key.pem" ]; then
  umask 077
  openssl req -x509 -newkey rsa:4096 -nodes -days 365 \
    -subj "/CN=${DOVECOT_TLS_COMMON_NAME:-mail.example.test}" \
    -keyout "$tls_dir/key.pem" -out "$tls_dir/cert.pem"
fi

chmod 0600 "$tls_dir/key.pem"
exec "$@"
