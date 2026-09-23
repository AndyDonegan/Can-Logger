import lindllresource.LINdll;
import lindllresource.model.LINWorkingFrame;
import com.microchip.mcu8.communications.controllers.USBHID4Controller;
import com.microchip.mcu8.communications.core.Controller;
import javafx.collections.ListChangeListener;
import java.io.*;
import java.util.Locale;

/** Receive-only bridge for the Microchip APG LIN USB analyzer. */
public final class MicrochipLinReceiver {
    // The vendor frame parser peeks at the same status queue whose command API
    // removes replies. Retain the last real snapshot for nonblocking reads.
    // Timed poll still requires a queued reply, preserving request timeouts.
    static final class StatusQueue extends java.util.concurrent.LinkedBlockingQueue<byte[]> {
        private volatile byte[] lastStatus;
        @Override public void put(byte[] value) throws InterruptedException {
            lastStatus = value.clone();
            super.put(value);
        }
        @Override public byte[] remove() {
            byte[] current = super.poll();
            if (current != null) return current;
            if (lastStatus != null) return lastStatus;
            throw new java.util.NoSuchElementException();
        }
        @Override public byte[] peek() {
            byte[] current = super.peek();
            return current != null ? current : lastStatus;
        }
    }

    private static String hex(byte[] bytes, int count) {
        StringBuilder result = new StringBuilder(count * 2);
        for (int i = 0; i < Math.min(count, bytes.length); i++) result.append(String.format("%02X", bytes[i] & 255));
        return result.toString();
    }

    @SuppressWarnings({"rawtypes", "unchecked"})
    public static void main(String[] args) {
        final PrintStream output = System.out;
        System.setOut(System.err); // Keep vendor diagnostics out of the protocol stream.
        Controller usb = null;
        LINdll lin = null;
        int result = 0;
        int seconds = 0, diagnosticBaud = 0, initialBaud = 19600;
        try {
            if (args.length == 1 && args[0].equals("--test-status-queue")) {
                StatusQueue queue = new StatusQueue();
                if (queue.peek() != null) throw new AssertionError("Invented initial status");
                queue.put(new byte[] { 7 });
                if (queue.remove()[0] != 7 || queue.peek()[0] != 7 || queue.remove()[0] != 7)
                    throw new AssertionError("Consumed status snapshot lost");
                if (queue.poll(20, java.util.concurrent.TimeUnit.MILLISECONDS) != null)
                    throw new AssertionError("Cached status accepted as a new reply");
                queue.put(new byte[] { 9 });
                if (queue.poll(20, java.util.concurrent.TimeUnit.MILLISECONDS)[0] != 9 || queue.peek()[0] != 9)
                    throw new AssertionError("New status not retained");
                output.println("Status queue checks passed");
                return;
            }
            for (int i = 0; i < args.length; i += 2) {
                if (i + 1 >= args.length) throw new IllegalArgumentException("Missing option value");
                int value = Integer.parseInt(args[i + 1]);
                if (args[i].equals("--seconds") && value >= 1 && value <= 120) seconds = value;
                else if (args[i].equals("--initial-baud") && (value == 10000 || value == 19200 || value == 19600)) initialBaud = value;
                else if (args[i].equals("--diagnostic-baud") && (value == 10000 || value == 19200 || value == 19600)) diagnosticBaud = value;
                else throw new IllegalArgumentException("Unknown option or unsupported diagnostic value");
            }
            if (diagnosticBaud != 0 && seconds == 0) throw new IllegalArgumentException("Baud experiments require a bounded --seconds session");
            usb = new USBHID4Controller("A04");
            // Trace the existing transport without opening another device handle or changing packets.
            final com.microchip.mcu8.communications.core.CommInterface communication = usb.communicationClass;
            usb.communicationClass = (com.microchip.mcu8.communications.core.CommInterface)
                java.lang.reflect.Proxy.newProxyInstance(communication.getClass().getClassLoader(),
                    new Class<?>[] { com.microchip.mcu8.communications.core.CommInterface.class },
                    (proxy, method, parameters) -> {
                        Object resultValue;
                        try { resultValue = method.invoke(communication, parameters); }
                        catch (java.lang.reflect.InvocationTargetException ex) { throw ex.getCause(); }
                        if (parameters != null && parameters.length > 0 && parameters[0] instanceof byte[] && resultValue instanceof Integer) {
                            int count = (Integer) resultValue;
                            if (count > 0) output.println("RAW_USB\t" + System.currentTimeMillis() + "\t" +
                                method.getName() + "\t" + count + "\t" + hex((byte[]) parameters[0], count));
                        }
                        return resultValue;
                    });
            usb.transport.setLowerLevelCommunicationClass(usb.communicationClass);
            lin = new LINdll(usb);
            lin.getModel().status = new StatusQueue();
            lin.getModel().readLINFrames.addListener(new ListChangeListener<LINWorkingFrame>() {
                public void onChanged(Change<? extends LINWorkingFrame> change) {
                    while (change.next()) for (LINWorkingFrame frame : change.getAddedSubList()) {
                        int count = frame.FrameInfo.bytecount & 255;
                        byte[] bytes = frame.FrameInfo.FrameData;
                        if (bytes == null || count > bytes.length || count > 9) {
                            output.println("ERROR\tInvalid frame length from analyzer: " + count);
                            continue;
                        }
                        StringBuilder hex = new StringBuilder();
                        for (int i = 0; i < count; i++) hex.append(String.format("%02X", bytes[i] & 255));
                        output.printf(Locale.ROOT, "FRAME\t%d\t%d\t%d\t%.6f\t%d\t%s%n",
                            System.currentTimeMillis(), frame.FrameInfo.FrameID & 255,
                            frame.FrameInfo.baud & 65535, frame.FrameInfo.time,
                            frame.FrameInfo.m_OnReceive_error, hex.toString());
                        output.flush();
                    }
                }
            });
            lin.startReadContinuos();
            output.println("CONFIG_BEFORE\t" + hex(lin.getController().getCommandFactory().getGet_Status_Packet().execute(), 65));
            lin.getController().getCommandFactory().getConfigurePICKitSerial().execute(10, true);
            lin.getController().getCommandFactory().getSet_Buffer_Flush_Parameters().execute(true, true, (byte)10, 10);
            lin.getController().getCommandFactory().getSetModeDisplayAll().execute();
            int startingBaud = diagnosticBaud != 0 ? diagnosticBaud : initialBaud;
            output.println("INITIAL_BAUD\t" + startingBaud);
            lin.getController().getCommandFactory().getChangeLINBaudRate().execute(startingBaud);
            output.println("CONFIG_AFTER\t" + hex(lin.getController().getCommandFactory().getGet_Status_Packet().execute(), 65));
            output.println("CONFIG_BAUD\t" + (lin.getController().getCommandFactory().getGet_LIN_Baud_Rate().execute() & 65535));
            output.println("READY\tMicrochip LIN receiver connected");
            output.flush();
            final java.util.concurrent.atomic.AtomicBoolean stop = new java.util.concurrent.atomic.AtomicBoolean();
            Thread inputThread = new Thread(() -> {
                try {
                    BufferedReader input = new BufferedReader(new InputStreamReader(System.in));
                    String line;
                    while ((line = input.readLine()) != null && !line.equals("STOP")) { }
                } catch (IOException ignored) { }
                stop.set(true);
            }, "parent-input");
            inputThread.setDaemon(true);
            long deadline = Long.MAX_VALUE;
            if (seconds > 0)
                deadline = System.currentTimeMillis() + seconds * 1000L;
            else inputThread.start();
            long nextHealthCheck = System.currentTimeMillis() + 3000;
            while (!stop.get() && System.currentTimeMillis() < deadline) {
                Thread.sleep(100);
                if (System.currentTimeMillis() >= nextHealthCheck) {
                    // USB status only: no LIN header, response, wakeup or schedule is sent.
                    lin.getController().getCommandFactory().getGet_Status_Packet().execute();
                    output.println("HEARTBEAT");
                    output.flush();
                    nextHealthCheck = System.currentTimeMillis() + 3000;
                }
            }
        } catch (Throwable ex) {
            ex.printStackTrace(System.err);
            output.println("ERROR\t" + ex.toString().replace('\n', ' ').replace('\t', ' '));
            result = 1;
        } finally {
            // Restore the baseline after a bounded USB configuration experiment.
            if (diagnosticBaud != 0 && lin != null) {
                try {
                    lin.getController().getCommandFactory().getChangeLINBaudRate().execute(10000);
                    output.println("RESTORED_BAUD\t10000");
                } catch (Throwable ex) {
                    output.println("ERROR\tCould not restore baseline baud: " + ex.toString());
                    result = 1;
                }
            }
            try { if (lin != null) lin.stopReadContinuos(); } catch (Throwable ignored) { }
            try { if (usb != null) usb.Disconnect(); } catch (Throwable ignored) { }
        }
        System.exit(result);
    }
}
