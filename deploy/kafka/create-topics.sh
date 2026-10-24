#!/bin/sh
# Creates every topic Viatica publishes to, plus a dead letter twin for each. The broker runs with automatic
# topic creation off, so a misspelt topic name fails at the producer instead of quietly creating a topic nobody
# consumes.
#
# Twelve partitions each: more than one consumer per service will ever need on this machine, and enough to show
# per-key ordering holding across partitions. Replication factor 1 because there is one broker.
#
# --if-not-exists makes the script safe to rerun against a broker that already has the topics.
set -eu

bootstrap="${KAFKA_BOOTSTRAP:-kafka:9092}"

for topic in stays.v1 cars.v1 flights.v1 booking.v1 payments.v1; do
  for name in "$topic" "$topic.dlq"; do
    /opt/kafka/bin/kafka-topics.sh --bootstrap-server "$bootstrap" --create --if-not-exists \
      --topic "$name" --partitions 12 --replication-factor 1
  done
done
