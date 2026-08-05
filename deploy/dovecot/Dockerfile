FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install --yes --no-install-recommends dovecot-core dovecot-imapd openssl tini \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 5000 vmail \
    && useradd --uid 5000 --gid 5000 --home-dir /var/mail/vhosts --no-create-home --shell /usr/sbin/nologin vmail \
    && mkdir -p /var/mail/vhosts /etc/dovecot/local \
    && chown -R vmail:vmail /var/mail/vhosts /etc/dovecot/local

COPY dovecot.conf /etc/dovecot/dovecot.conf
COPY docker-entrypoint.sh /usr/local/bin/dovecot-entrypoint
RUN chmod 0755 /usr/local/bin/dovecot-entrypoint

EXPOSE 993
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/dovecot-entrypoint"]
CMD ["dovecot", "-F", "-c", "/etc/dovecot/dovecot.conf"]
