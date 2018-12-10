FROM ubuntu:18.04

RUN apt-get update && apt-get install -y \
  libssl1.1 \
  ca-certificates \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app
ADD update-identity-hook /app

CMD ["/app/update-identity-hook"]
